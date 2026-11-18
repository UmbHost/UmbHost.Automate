# UmbHost.Automate.Bluesky

Bluesky connection and **Create Bluesky Post** and **Get Bluesky Post Metrics** actions for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

Requires Umbraco Automate 17.4+ / 18.4+ and Umbraco CMS 17.6+.

**Checked live** on 2026-11-17: a text post, an image with alt text, a link card (its facet's byte offsets past an em dash), a two-part thread, a scheduled post, a wrong app password refused at sign-in (401, shown as "Invalid identifier or password") and then retried, and the metrics read from the public AppView (all five counts). The records the connector sends match what Bluesky stored, and the tests use those replies (`tests/UmbHost.Automate.Bluesky.Tests/Fixtures/Live`).

## Setup

1. In Bluesky, create an app password: **Settings → Privacy and security → App passwords**. Never use the account password.
2. Put it in config (user secrets in development, environment variables on servers):

   ```jsonc
   { "Umbraco": { "Automate": { "Secrets": { "BlueskyAppPassword": "xxxx-xxxx-xxxx-xxxx" } } } }
   ```

   Environment variable form: `Umbraco__Automate__Secrets__BlueskyAppPassword`.
3. **Automate → Connections → Create → Bluesky.** Enter the handle; the App Password field already points at `$Umbraco:Automate:Secrets:BlueskyAppPassword`. **Validate** signs in.

## Create Bluesky Post

| Setting | Notes |
|---|---|
| Text | Up to 300 characters. URLs in the text are made clickable (Bluesky needs explicit link facets for that). |
| Image URL | Optional https URL of a JPEG, PNG, GIF or WebP, up to 2 MB. Downloaded through Automate's SSRF-protected client. |
| Image Alt Text | Describes the image for screen readers. |
| Link URL | Optional https link shown as a card under the post. A post with an image shows the image instead (Bluesky takes one or the other). |
| Link Title / Link Description | The card's text. |
| Link Image URL | Optional https URL of the card's thumbnail, up to 1 MB, downloaded through the same SSRF-protected client. If it can't be fetched, the card is posted without a thumbnail. |
| Language | Optional, e.g. `en`. |

Output: `uri` (at://), `cid`, `postUrl` (bsky.app link).

Link preview cards aren't generated: Bluesky doesn't fetch pages, so a card would mean scraping the page's metadata.

## Get Bluesky Post Metrics

Reads posts' public counts from Bluesky's public AppView (`app.bsky.feed.getPosts`, 25 per call). The read is unauthenticated: the connection only says which account (and so which channel) the automation belongs to.

| Setting | Notes |
|---|---|
| Ids | A JSON array of 1 to 100 post at-uris, e.g. `["at://did:plc:…/app.bsky.feed.post/…"]`. An at-uri with a handle is resolved to a DID first. |

Output: `results`, one per id in order, each with `id`, `state` (`ok` or `gone`), `values` (`likeCount`, `repostCount`, `replyCount`, `quoteCount`, `bookmarkCount`) and `reason`. A post Bluesky no longer returns, or a malformed id, is `gone`; it never fails the step. A 429, 5xx or timeout fails the step and Automate retries it (a read is safe to repeat).

## Retries

Bluesky has no idempotency key. Signing in and uploading are retried as normal, but if the post request itself fails without a clear answer (5xx, timeout) the step fails **without retrying**, because the post may already exist. The error says so; check the account before re-running.
