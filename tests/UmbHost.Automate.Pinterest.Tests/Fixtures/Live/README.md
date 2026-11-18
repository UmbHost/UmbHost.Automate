# Live Pinterest sandbox replies

Real answers from the Pinterest sandbox (`https://api-sandbox.pinterest.com/v5/`), captured on 2026-11-18 with the live test's sandbox access token, after the live test made its three Pins. Tests read them through `LiveFixture`.

| File | Request | What it is |
|---|---|---|
| `user-account.json` | `GET /v5/user_account` | The business account. |
| `boards.json` | `GET /v5/boards?page_size=250` | The one board, on one page (`bookmark` null, so nothing more to follow). |
| `board-pins.json` | `GET /v5/boards/{board_id}/pins` | The board's three Pins: an image Pin with a title, description, link and alt text, a scheduled Pin, and the Pin retried after a swapped token. Each has `"pin_metrics": null`. |
| `pin-with-metrics.json` | `GET /v5/pins/{pin_id}?pin_metrics=true` | One Pin with its metrics: `90d` and `lifetime_metrics`, which send `outbound_click` (not the API description's `clickthrough`), `save`, and `profile_visit` and `user_follow` as null. |
| `error-401.json` | `GET /v5/user_account` with the bogus token `x` | The 401 body, byte for byte. |

By the time these were captured every Pin had its four image sizes (`media.images`); during the live run a sandbox Pin was seen without them, so the tests also cover a Pin answer with no `media.images` and one with no `media` at all.

## Redacted

- The token: never stored; the capture script read it from user secrets and sent it only in the request header.
- Account id → `100000000000000001`, username → `example`, business name → `Example business`, website → `http://www.example.com`.
- Board id → `200000000000000001`; Pin ids → `300000000000000001` to `300000000000000003`, in creation order.
- Image file hashes in the `i.pinimg.com` URLs → placeholder hex.

Everything else, field names, nulls, texts and counts included, is as the sandbox sent it. Errors are stored compact, as sent; the rest are indented.
