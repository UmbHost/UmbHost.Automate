# UmbHost.Automate.Pinterest

Pinterest connection (OAuth) and a **Create Pin** action for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

Requires Umbraco Automate 17.5+ / 18.5+, `Umbraco.Automate.OpenIddict` 17.1.5+, and Umbraco CMS 17.6+.

## Testing status

This package is tested against mocked Pinterest API responses only, built from Pinterest's published API v5 description. It has not been run against the live Pinterest API.

## Setup

1. With a Pinterest business account, create an app in [My apps](https://developers.pinterest.com/apps/) and wait for Pinterest to approve it. A new app starts with trial access, and Pins made with trial access are visible only to you; apply for standard access before going live.
2. Add the redirect URI `https://<your-site>/umbraco/automate/oauth/callback/pinterest`.
3. Config, with the app id and app secret key:

   ```jsonc
   {
     "Umbraco": {
       "Automate": {
         "Providers": {
           "Pinterest": { "ClientId": "...", "ClientSecret": "..." }
         }
       }
     }
   }
   ```

   The package requests `boards:read` and `pins:write`. Access tokens last 30 days and renew automatically with the refresh token. Without a `ClientId` the provider is skipped and everything else still loads.
4. **Automate → Connections → Create → Pinterest → Authorize.** **Validate** lists your boards as "Name (id)".

## Create Pin

| Setting | Notes |
|---|---|
| Board ID | The numeric id of the board, from **Validate**. |
| Image URL | Required. A public https URL of a JPEG or PNG; Pinterest fetches it. |
| Title | Optional, up to 100 characters. |
| Description | Optional, up to 800 characters. |
| Link | Optional URL the Pin opens. |
| Image Alt Text | Optional, up to 500 characters. |

Output: `postId`, `postUrl`.

## Retries

Pinterest has no idempotency key. If the create request fails without a clear answer the step fails **without retrying**; check the board before re-running.
