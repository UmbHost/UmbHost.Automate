# Live Mastodon replies

Real answers from umbracocommunity.social (Mastodon 4.7.5), captured on 2026-11-18 after the UmbSocial live test posted its Mastodon set there. Tests read them through `LiveFixture`.

| File | Request | What it is |
|---|---|---|
| `status-plain.json` | `GET /api/v1/accounts/{id}/statuses?limit=20` | A public text status (test 4, scheduled). |
| `status-image.json` | same | A status with one image `media_attachments` entry (`meta.original` 1200x675, `description` = the alt text). |
| `status-link-card.json` | same | A status with a link and its preview `card`. |
| `status-unlisted.json` | same | An unlisted status. |
| `status-content-warning-thread-start.json` | same | The first part of a thread, behind a content warning (`spoiler_text`, `sensitive`), with one reply. |
| `status-thread-reply.json` | same | The second part, a reply to the first (`in_reply_to_id`), behind the same warning. |
| `instance-v2.json` | `GET /api/v2/instance` | The instance's version and `configuration` limits. |
| `error-401-invalid-token.json` | `GET /api/v1/accounts/verify_credentials` with the bogus token `x` | The 401 body, byte for byte. The same body was in the test site's log when the live post used a swapped token. |

The status files are entries of the account's timeline, so each is a whole Status entity, the same entity `POST /api/v1/statuses` answers with. The MediaAttachment inside `status-image.json` is the entity `POST /api/v2/media` answers with.

## Redacted

- Account: id → `100000000000000001`, username and `acct` → `example` (also in each `url`).
- Status ids → `110000000000000001` to `110000000000000008`, in posting order (`in_reply_to_id` follows); media id → `120000000000000001` (also in the media path), file name → `0123456789abcdef`.
- `instance-v2.json`: the contact email → `admin@example.com`, the contact account → a placeholder `admin` with the same fields, its counts zeroed, the active-user count → 0, and the handles in the rules → `@admin or @moderator`.

Everything else, field names, field order, nulls and the post texts included, is as the instance sent it. Errors are stored compact, as sent; the rest are indented.
