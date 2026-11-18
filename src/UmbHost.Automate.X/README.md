# UmbHost.Automate.X

X (Twitter) connection (OAuth 2.0) and **Create X Post** and **Get X Post Metrics** actions for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

Requires Umbraco Automate 17.4+ / 18.4+, `Umbraco.Automate.OpenIddict`, and Umbraco CMS 17.6+.

## Cost

The X API is pay-per-use. Each post is billed, and **a post containing a link is billed at a much higher rate** than a plain one. Check [X's pricing](https://docs.x.com/x-api/getting-started/pricing) before automating link posts.

**Reading metrics costs money too:** $0.005 for each post returned (not per request). Re-reading a post in the same UTC day isn't charged again, though X calls that a soft guarantee. Reads are capped at 3 million posts a month. So metrics are off unless **both** switches are on:

- this provider's `Umbraco:Automate:Providers:Twitter:Insights` (default `false`; only `true` turns it on). While it's off, **Get X Post Metrics** sends nothing to X, not even a token refresh, and returns every id as `needsPermission` with reason `insights_off`;
- for UmbSocial, **X metrics in Social › Settings** (default off). UmbSocial doesn't run the metrics automation while it's off.

## Testing status

This package is tested against mocked X API responses only, built from X's published API documentation. It has not been run against the live X API.

## Setup

1. In the [X developer console](https://developer.x.com), create an app with OAuth 2.0 enabled as a confidential client (Web App).
2. Add the callback URL `https://<your-site>/umbraco/automate/oauth/callback/twitter`.
3. Config. OpenIddict still calls the provider **Twitter**, so that's the config key:

   ```jsonc
   {
     "Umbraco": {
       "Automate": {
         "Providers": {
           "Twitter": { "ClientId": "...", "ClientSecret": "..." }
         }
       }
     }
   }
   ```

   The package requests `tweet.read tweet.write users.read media.write offline.access` itself. `offline.access` gives a refresh token, so the two-hour access tokens renew automatically. Without a `ClientId` the provider is skipped and everything else still loads.
4. **Automate → Connections → Create → X (Twitter) → Authorize.** **Validate** calls `GET /2/users/me`.

## Create X Post

| Setting | Notes |
|---|---|
| Text | 280 weighted characters; links count as 23, emoji and CJK as 2. X rejects longer posts. |
| Image URL | Optional https URL of a JPEG, PNG or WebP up to 5 MB, or a GIF (animated or not) up to 15 MB, uploaded with X's chunked upload as `tweet_gif`. |
| Image Alt Text | Up to 1000 characters. |

Output: `postId`, `postUrl`.

## Get X Post Metrics

Reads your own posts' metrics with `GET /2/tweets?ids=…&tweet.fields=public_metrics,non_public_metrics,organic_metrics` (up to 100 ids per call), using the connection's existing `tweet.read` and `users.read` scopes. Off by default; see [Cost](#cost).

```jsonc
{ "Umbraco": { "Automate": { "Providers": { "Twitter": { "Insights": true } } } } }
```

| Setting | Notes |
|---|---|
| Ids | A JSON array of 1 to 100 post ids. |

Output: `results`, one per id in order, each with `id`, `state`, `values` and `reason`. Values use X's names: `impression_count`, `like_count`, `retweet_count`, `reply_count`, `quote_count` and `bookmark_count`, plus `url_link_clicks` and `user_profile_clicks` for posts from the last 30 days (X stops returning them after that). A post X can't find is `gone`; one X withholds for another reason is `unavailable` with reason `scope_missing`. A 401 fails the step. A 408, 429, 5xx or timeout fails it and Automate retries.

## Retries

X has no idempotency key. If the post request fails without a clear answer the step fails **without retrying**; check the account before re-running.
