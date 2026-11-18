# Live Threads replies

Real answers from the Threads API during the UmbSocial live test on 2026-11-18. Tests read them through `LiveFixture`.

| File | Where it came from | What it is |
|---|---|---|
| `error-too-many-links.json` | The test site's log, 2026-11-18 10:17 UTC: `POST /v1.0/{user-id}/threads` (the container) for a post with more than five links | The 400 body, byte for byte apart from `fbtrace_id`. |

Threads answers only appear in the site log when a step fails, so this is the only body captured. The log's timings are worth knowing: the six live posts that went through took 12.0 to 28.6 seconds each (container, status checks until `FINISHED`, publish, permalink), and the refused one failed in 0.7 seconds. The tests cover a container that is still `IN_PROGRESS` on the first checks.

## Redacted

- `fbtrace_id` → `REDACTED_FBTRACE_ID`.

Errors are stored compact, as sent.
