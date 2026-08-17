---
name: embed-redleaf-entity
description: "Resolve an authorized RedLeaf entity by ID, name, slug, or type and emit its canonical Markdown entity card or inline reference. Use when a user asks to show, embed, inspect, link, mention, or surface a Leaf entity in chat or a RedLeaf Page."
---

# Embed a RedLeaf entity

Resolve the entity before emitting an embed. Never guess an ID or copy entity properties into the
Markdown.

Run the bundled resolver from this Skill directory:

```bash
node scripts/find-entity.cjs --query "Standard" --type quality-mode
node scripts/find-entity.cjs --id 2795e49f-4087-e052-be15-7973309836f2
```

The resolver requires `REDLEAF_EXECUTION_TOKEN`, sends it only to RedLeaf at
`http://127.0.0.1:18804`, and returns identity rather than private entity data. Its origin cannot be
overridden. Stop with `authentication_required` when the token is absent. Never retry through local
fallback.

Treat `matched` as resolved. For `ambiguous`, disambiguate from context or ask one short question.
Do not emit a card for `candidate` or `not_found` without confirmation.

For a card, copy the returned `embed` value as a standalone Markdown paragraph:

```markdown
[Standard](redleaf://quality-mode/2795e49f-4087-e052-be15-7973309836f2)
```

Keep a blank line before and after a card. A `redleaf://` reference mixed into prose renders as a
normal link. For Page authoring, append `?display=preview` for a rich preview,
`?display=inline` for an inline mention, or `?field={field_key}` for a live field value only when the
user requests that mode. Do not use image syntax or invent another entity protocol.
