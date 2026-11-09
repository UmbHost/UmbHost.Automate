# UmbHost.Automate.Mastodon

Mastodon connection and a **Create Mastodon Status** action for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate). Works with any Mastodon instance.

Requires Umbraco Automate 17.4+ / 18.4+ and Umbraco CMS 17.6+.

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

## Retries

The status request carries an `Idempotency-Key` made from the run and step ids, which Mastodon honours for an hour. Automate's retries reuse the same key, so a retried step returns the original status instead of posting twice.
