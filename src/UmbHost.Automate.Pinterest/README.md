# UmbHost.Automate.Pinterest

Pinterest connection (OAuth) and **Create Pin** and **Get Pinterest Pin Metrics** actions for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

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

   The package requests `boards:read`, `boards:write`, `pins:read` and `pins:write`, the scopes Pinterest's API description lists for creating a Pin. Access tokens last 30 days and renew automatically with the refresh token. Without a `ClientId` the provider is skipped and everything else still loads.
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

## Get Pinterest Pin Metrics

Reads each Pin's lifetime metrics with one `GET /v5/pins/{pin_id}?pin_metrics=true` per id (rate category `org_read`), using the connection's existing `pins:read` and `boards:read` scopes. Trial-tier apps get 1,000 requests a day in all, and this action uses one request per Pin.

| Setting | Notes |
|---|---|
| Ids | A JSON array of 1 to 100 Pin ids. |

Output: `results`, one per id in order, each with `id`, `state`, `values` and `reason`. Values use the names Pinterest's `pin_metrics` sends: `impression`, `save` (when Pinterest includes it), `pin_click`, `clickthrough` (outbound clicks), `reaction` and `comment`. Other outcomes:

- A Pin Pinterest can't find (404) is `gone`.
- A Pin the account can't read (403) is `needsPermission` with reason `scope_missing`.
- A Pin with no lifetime metrics is `unavailable` with reason `unsupported`. Pinterest only has them for video and Idea Pins created before 20 March 2023.
- A 401 fails the step. A 408, 429, 5xx or timeout fails it and Automate retries.

## Retries

Pinterest has no idempotency key. If the create request fails without a clear answer the step fails **without retrying**; check the board before re-running.
