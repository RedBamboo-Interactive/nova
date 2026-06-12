"""One-time migration: nova.db -> RedLeaf.

Discussions become `discussion` entities (versioning off), conversation
records become `nova-messages` records with original timestamps preserved.
Safe to re-run for discussions (upsert by slug); only run the message phase
once per machine (use --discussions-only to refresh entities).

Usage: python migrate-nova-to-redleaf.py [--dry-run] [--discussions-only]
"""

import json
import os
import re
import sqlite3
import sys
import time
import urllib.request

# 127.0.0.1, not localhost: Kestrel binds IPv4 only, and Windows' IPv6-first
# localhost resolution costs ~1s of connect fallback per request.
REDLEAF = "http://127.0.0.1:18804"
BATCH = 500
DB = os.path.join(os.environ["LOCALAPPDATA"], "Nova", "nova.db")

DRY_RUN = "--dry-run" in sys.argv
DISCUSSIONS_ONLY = "--discussions-only" in sys.argv


def api(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    for attempt in range(6):
        req = urllib.request.Request(
            REDLEAF + path, data=data,
            headers={"Content-Type": "application/json"}, method=method,
        )
        try:
            with urllib.request.urlopen(req, timeout=60) as resp:
                return resp.status, json.loads(resp.read() or b"null")
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read() or b"null")
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            if attempt == 5:
                raise
            wait = 5 * (attempt + 1)
            print(f"  connection failed ({e}); retrying in {wait}s...")
            time.sleep(wait)


def slugify(discussion_id):
    # must match NovaMirror.DiscussionSlug
    return "discussion-nova-" + re.sub(r"[^a-z0-9]", "-", discussion_id.lower())


def iso(sqlite_dt):
    # Nova stores DateTime.UtcNow as naive text — mark it UTC explicitly so
    # the API doesn't parse it as local time.
    if not sqlite_dt:
        return None
    s = sqlite_dt.replace(" ", "T")
    if "+" not in s and not s.endswith("Z"):
        s += "Z"
    return s


def ensure_schema():
    status, _ = api("GET", "/api/entity-types/discussion")
    if status == 404:
        status, body = api("POST", "/api/entity-types", {
            "name": "Discussion",
            "description": "Nova chat discussion",
            "icon": "fa-solid fa-comments",
            "color": "fuchsia",
            "versioning": False,
        })
        assert status in (200, 201), f"create discussion type failed: {status} {body}"
        print("created entity type discussion")

    status, _ = api("GET", "/api/entities/nova-messages")
    if status == 404:
        status, body = api("PUT", "/api/entities/by-slug/nova-messages", {
            "name": "Nova Messages",
            "type_slug": "stream",
            "data": {
                "description": "Chat messages from Nova discussions",
                "parent_type": "discussion",
                "app": "nova",
            },
        })
        assert status in (200, 201), f"register nova-messages stream failed: {status} {body}"
        print("registered stream nova-messages")


def migrate():
    conn = sqlite3.connect(DB)
    conn.row_factory = sqlite3.Row

    discussions = conn.execute("SELECT * FROM Discussions").fetchall()
    print(f"{len(discussions)} discussion(s)")
    entity_ids, failures = {}, 0

    for d in discussions:
        data = {
            "discussion_id": d["Id"],
            "status": d["Status"],
            "owner_id": d["OwnerId"],
            "session_id": d["SessionId"],
            "message_count": d["MessageCount"],
            "created_at": iso(d["CreatedAt"]),
            "last_activity": iso(d["LastActivity"]),
            "app": "nova",
        }
        name = d["Title"] or f"Discussion {d['Id']}"
        if DRY_RUN:
            entity_ids[d["Id"]] = "dry"
            continue
        status, body = api("PUT", f"/api/entities/by-slug/{slugify(d['Id'])}",
                           {"name": name, "type_slug": "discussion", "data": data})
        if status in (200, 201):
            entity_ids[d["Id"]] = body["id"]
        else:
            failures += 1
            print(f"  discussion {d['Id']} failed: {status} {body}")

    print(f"discussions upserted: {len(entity_ids)}, failed: {failures}")
    if DISCUSSIONS_ONLY:
        conn.close()
        return

    total = conn.execute("SELECT COUNT(*) FROM Conversations").fetchone()[0]
    print(f"{total} message(s)")

    sent = 0
    batch = []

    def flush():
        nonlocal sent, batch
        if not batch or DRY_RUN:
            batch = []
            return
        status, body = api("POST", "/api/streams/nova-messages/records", {"records": batch})
        if status != 200:
            print(f"  batch failed: {status} {body}")
        else:
            sent += body["created"]
        batch = []

    for m in conn.execute("SELECT * FROM Conversations ORDER BY Id"):
        record = {
            "data": {
                "discussion_id": m["ContextId"],
                "role": m["Role"],
                "content": m["Content"],
                "parts_json": m["PartsJson"],
                "source": m["Source"],
                "timestamp": iso(m["Timestamp"]),
            },
            "created_at": iso(m["Timestamp"]),
        }
        if m["UserId"]:
            record["user_id"] = m["UserId"]
        eid = entity_ids.get(m["ContextId"])
        if eid and eid != "dry":
            record["entity_id"] = eid
        batch.append(record)
        if len(batch) >= BATCH:
            flush()
            if sent and sent % 10000 < BATCH:
                print(f"  ... {sent}/{total}")
    flush()
    print(f"messages migrated: {sent}")
    conn.close()


if __name__ == "__main__":
    if not DRY_RUN:
        ensure_schema()
    migrate()
    print("done" + (" (dry run)" if DRY_RUN else ""))
