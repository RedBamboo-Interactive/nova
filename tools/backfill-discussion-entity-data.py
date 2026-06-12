"""One-off: re-upsert all discussion entities with title + last_read_at.

The NovaMirror data payload gained `title` (nullable, the entity name holds
a placeholder for untitled discussions so null can't be recovered from it)
and `last_read_at` (used by the unread logic). Existing entities in RedLeaf
predate those fields; this re-publishes every discussion from nova.db with
the full payload. Idempotent — PUT by-slug, versioning is off for the type.
"""

import json
import os
import re
import sqlite3
import urllib.request

REDLEAF = "http://127.0.0.1:18804"
DB = os.path.join(os.environ["LOCALAPPDATA"], "Nova", "nova.db")

_opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def slug(discussion_id):
    return "discussion-nova-" + re.sub(r"[^a-z0-9]", "-", discussion_id.lower())


def iso(sqlite_ts):
    """EF SQLite text timestamp ('YYYY-MM-DD HH:MM:SS.fffffff') -> ISO UTC."""
    if sqlite_ts is None:
        return None
    ts = sqlite_ts.strip().replace(" ", "T")
    # cap fractional seconds at 6 digits for fromisoformat compatibility
    ts = re.sub(r"(\.\d{6})\d+", r"\1", ts)
    return ts + "+00:00"


conn = sqlite3.connect(DB)
conn.row_factory = sqlite3.Row
rows = conn.execute("SELECT * FROM Discussions").fetchall()

updated = 0
for d in rows:
    body = {
        "name": d["Title"] or f"Discussion {d['Id']}",
        "type_slug": "discussion",
        "data": {
            "discussion_id": d["Id"],
            "title": d["Title"],
            "status": d["Status"],
            "owner_id": d["OwnerId"],
            "session_id": d["SessionId"],
            "message_count": d["MessageCount"],
            "created_at": iso(d["CreatedAt"]),
            "last_activity": iso(d["LastActivity"]),
            "last_read_at": iso(d["LastReadAt"]),
            "app": "nova",
        },
    }
    req = urllib.request.Request(
        f"{REDLEAF}/api/entities/by-slug/{slug(d['Id'])}",
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"}, method="PUT")
    with _opener.open(req, timeout=30) as resp:
        resp.read()
    updated += 1

print(f"re-upserted {updated} discussions")
