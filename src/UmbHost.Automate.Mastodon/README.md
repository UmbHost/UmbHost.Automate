# UmbHost.Automate.Mastodon

Mastodon connection and **Create Mastodon Status** and **Get Mastodon Post Metrics** actions for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate). Works with any Mastodon instance.

Requires Umbraco Automate 17.5+ / 18.5+ and Umbraco CMS 17.6+.

**Checked live** on 2026-11-18 against umbracocommunity.social (Mastodon 4.7.5): a text status, an image with alt text, a link (with its preview card), an unlisted status, a two-part thread behind a content warning, a scheduled status, a swapped token refused with 401 (shown as "The access token is invalid") and then retried, and the metrics read (all four counts, `quotes_count` included). The tests use those replies (`tests/UmbHost.Automate.Mastodon.Tests/Fixtures/Live`).

## Setup

1. On the instance: **Preferences → Development → New application**, with scopes `write:statuses` and `write:media`. Copy the access token.
2. Put it in config:

   ```jsonc
   { "Umbraco": { "Automate": { "Secrets": { "MastodonAccessToken": "..." } } } }
   ```

   Environment variable form: `Umbraco__Automate__Secrets__MastodonAccessToken`.
3. **Automate → Connections → Create → Mastodon.** Enter the instance URL (e.g. `https://mastodon.social`). **Validate** calls `verify_credentials`.

## Create Mastodon Status

| Setting | Notes |
|---|---|
| Text | Most instances allow 500 characters; each link counts as 23. |
| Visibility | `public` (default), `unlisted`, `private` or `direct`. |
| Image URL | Optional https URL of a JPEG, PNG, GIF or WebP. The action waits for the instance to finish processing it. |
| Image Alt Text | Describes the image for screen readers. |
| Language | Optional ISO 639-1 code. |

Output: `statusId`, `postUrl`.

## Get Mastodon Post Metrics

Reads statuses' public counts from the connection's instance: `GET /api/v1/statuses?id[]=…` (Mastodon 4.3.0+, 20 per call), or one status at a time on older instances. No token is sent (the connection's token only has write scopes), so only public and unlisted statuses can be read.

| Setting | Notes |
|---|---|
| Ids | A JSON array of 1 to 100 status ids on the connection's instance. |

Output: `results`, one per id in order, each with `id`, `state`, `values` (`favourites_count`, `reblogs_count`, `replies_count`, and `quotes_count` from 4.5.0) and `reason`. A deleted or private status is `gone` (Mastodon answers both the same way without a token). An instance in authorized-fetch mode is `unavailable` with reason `scope_missing`. A redirect is never followed: the ids are `unavailable` with reason `redirected`. A 408, 429, 5xx or timeout fails the step and Automate retries it.

Connections are set up by site administrators. The instance URL must be the instance's real `https://` host (for example `https://mastodon.social`), not an address that redirects to it; an `http://` URL is refused as a configuration error.

## Retries

The status request carries an `Idempotency-Key` made from the run and step ids, which Mastodon honours for an hour. Automate's retries reuse the same key, so a retried step returns the original status instead of posting twice.
