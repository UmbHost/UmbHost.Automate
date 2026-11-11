# UmbHost.Automate.Threads

Threads connection (OAuth) and **Create Threads Post** and **Get Threads Post Metrics** actions for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

Requires Umbraco Automate 17.5+ / 18.5+, `Umbraco.Automate.OpenIddict` 17.1.5+, and Umbraco CMS 17.6+.

## Testing status

This package is tested against mocked Threads API responses only, built from Meta's published Threads API documentation. It has not been run against the live Threads API.

## Setup

1. In [Meta for Developers](https://developers.facebook.com/apps/), create an app with the **Access the Threads API** use case. Add the permissions `threads_basic`, `threads_content_publish` and `threads_manage_replies`.
2. In the use case's settings, add the redirect callback URL `https://<your-site>/umbraco/automate/oauth/callback/threads`.
3. Until the app passes Meta's app review, only Threads accounts added as testers can be connected.
4. Config, with the **Threads app ID** and **Threads app secret** from the use case's settings (not the Meta app's own id and secret):

   ```jsonc
   {
     "Umbraco": {
       "Automate": {
         "Providers": {
           "Threads": { "ClientId": "...", "ClientSecret": "..." }
         }
       }
     }
   }
   ```

   Without a `ClientId` the provider is skipped and everything else still loads.
5. **Automate → Connections → Create → Threads → Authorize.** **Validate** names the account.

## Tokens

Threads issues a one-hour token at sign-in and no refresh token. The package swaps it for a 60-day token during the sign-in itself, before Automate stores it, and refreshes that token whenever it is used and is more than a week old. A connection that goes unused for 60 days has to be authorised again.

## Create Threads Post

| Setting | Notes |
|---|---|
| Text | Up to 500 characters; Threads counts each emoji as its UTF-8 bytes. Links in the text are clickable. |
| Image URL | Optional public https URL of a JPEG or PNG, up to 8 MB. Threads fetches it. |
| Image Alt Text | Up to 1000 characters. |
| Thread Parts | Optional JSON array of further posts (max 10), each posted as a reply to the one before. |

Output: `postId`, `postUrl`, `threadUrls`.

## Retries

Threads has no idempotency key. Nothing is live until a post is published, so a failure before that retries as normal; if the publish request fails without a clear answer the step fails **without retrying**, and a thread that fails part-way says which parts are live.

## Get Threads Post Metrics

Reads posts' insights, one call per post, with `GET /v1.0/{media-id}/insights?metric=views,likes,replies,reposts,quotes,shares` ([docs](https://developers.facebook.com/docs/threads/insights)), under Threads' own names: `views`, `likes`, `replies`, `reposts`, `quotes` and `shares`.

| Setting | Notes |
|---|---|
| Ids | JSON array of 1 to 100 Threads media ids: the `postId` **Create Threads Post** outputs, the thread's first post. Anything else comes back `gone` and is never sent. |

Output: `results`, one per id: `state` (`ok`, `gone`, `unavailable`, `needsPermission`), `values` and `reason`.

**Off until Meta approves the app.** Insights need the `threads_manage_insights` permission, which needs App Review with a screencast of the post metrics shown in your site, and is for business apps only ([permissions](https://developers.facebook.com/docs/permissions)). Add it to the use case's permissions, get it approved, then turn the metrics on:

```jsonc
{ "Umbraco": { "Automate": { "Providers": { "Threads": { "Insights": true } } } } }
```

Only `true` (in any case, surrounding spaces ignored) turns it on; anything else, or no value, leaves it off. While it's off the action sends nothing to Threads, not even a token refresh, and returns every id as `needsPermission` with reason `insights_off`.

**Existing connections must be authorised again** after you turn it on. Turning it on adds `threads_manage_insights` to what new authorisations ask for, but a connection authorised before that doesn't have it, and its posts come back `needsPermission` with reason `scope_missing` until it is re-authorised.

Things to know:

- Meta marks `views` and `shares` as "in development".
- Replies further down a thread aren't counted, so the action reads the thread's first post.
- A repost comes back with no insights, as `unavailable` with reason `unsupported`.
- Any other 4xx for a post (except 401, 408 and 429) is `unavailable` with reason `rejected` for that post alone; the rest of the batch is still read. An expired token fails the step; throttling, a 5xx or a timeout fails it and Automate retries.
- Not confirmed in Meta's docs: how long the figures lag and how long Threads keeps them, and which errors mean "missing permission" or "post gone" for Threads. The package uses the Graph API's codes: 10 and 200-299 for a missing permission, 100 with subcode 33 (or a 404) for a missing post.
