# UmbHost.Automate.X

X (Twitter) connection (OAuth 2.0) and a **Create X Post** action for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

Requires Umbraco Automate 17.4+ / 18.4+, `Umbraco.Automate.OpenIddict`, and Umbraco CMS 17.6+.

## Cost

The X API is pay-per-use. Each post is billed, and **a post containing a link is billed at a much higher rate** than a plain one. Check [X's pricing](https://docs.x.com/x-api/getting-started/pricing) before automating link posts.

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
| Image URL | Optional https URL of a JPEG, PNG, GIF or WebP, up to 5 MB. |
| Image Alt Text | Up to 1000 characters. |

Output: `postId`, `postUrl`.

## Retries

X has no idempotency key. If the post request fails without a clear answer the step fails **without retrying**; check the account before re-running.
