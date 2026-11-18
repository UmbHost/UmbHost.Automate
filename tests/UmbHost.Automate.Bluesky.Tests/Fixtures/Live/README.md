# Live Bluesky replies

Real answers from Bluesky, from the UmbSocial live test on 2026-11-17. Tests read them through `LiveFixture`.

| File | Where it came from | What it is |
|---|---|---|
| `author-feed.json` | `GET https://public.api.bsky.app/xrpc/app.bsky.feed.getAuthorFeed?actor=<handle>&limit=20`, captured 2026-11-18 (no token) | The seven posts the connector made on 2026-11-17: plain text, an image with alt text (`app.bsky.embed.images`), a link card (`app.bsky.embed.external`, with its link facet), a two-part thread (the reply's `reply.root` and `reply.parent`), and each PostView's `likeCount`, `replyCount`, `repostCount`, `quoteCount` and `bookmarkCount`. |
| `error-401-sign-in.json` | The test site's log, 2026-11-17 15:52 UTC: `com.atproto.server.createSession` with a wrong app password | The 401 body, byte for byte. |

The image post was made before images carried an aspect ratio (commit a39ec8c, the same afternoon), so its record has no `aspectRatio`.

## Redacted

- The account's DID → `did:plc:aaaaaaaaaaaaaaaaaaaaaaaa` (also inside every at-uri and CDN URL), its handle → `example.bsky.social`.

Record keys, CIDs, blob refs, texts and counts are as the AppView sent them. Errors are stored compact, as sent; the rest are indented.
