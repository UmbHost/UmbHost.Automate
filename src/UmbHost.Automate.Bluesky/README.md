# UmbHost.Automate.Bluesky

Bluesky connection and a **Create Bluesky Post** action for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

Requires Umbraco Automate 17.4+ / 18.4+ and Umbraco CMS 17.6+.

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
| Language | Optional, e.g. `en`. |

Output: `uri` (at://), `cid`, `postUrl` (bsky.app link).

Link preview cards aren't generated: Bluesky doesn't fetch pages, so a card would mean scraping the page's metadata.

## Retries

Bluesky has no idempotency key. Signing in and uploading are retried as normal, but if the post request itself fails without a clear answer (5xx, timeout) the step fails **without retrying**, because the post may already exist. The error says so; check the account before re-running.
