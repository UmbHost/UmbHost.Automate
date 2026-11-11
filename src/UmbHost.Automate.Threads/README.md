# UmbHost.Automate.Threads

Threads connection (OAuth) and a **Create Threads Post** action for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

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
