# UmbHost.Automate.Pinterest

Pinterest connection (OAuth or an access token, production or sandbox) and **Create Pin** and **Get Pinterest Pin Metrics** actions for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

Requires Umbraco Automate 17.5+ / 18.5+, `Umbraco.Automate.OpenIddict` 17.1.5+, and Umbraco CMS 17.6+.

## Testing status

**Checked live** on 2026-11-18 against the Pinterest sandbox, with an access token: an image Pin with a title, description, link and alt text, a scheduled Pin, and a swapped token refused with 401 (shown as the message to generate a new Sandbox token) and then retried. The account, board, Pin and Pin-metrics answers read afterwards showed outbound clicks as `outbound_click` (the API description's example says `clickthrough`), which the metrics now read. The tests use those answers (`tests/UmbHost.Automate.Pinterest.Tests/Fixtures/Live`); the other responses are mocked from Pinterest's published API v5 description. Production hasn't been checked live.

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
4. **Automate → Connections → Create → Pinterest → Authorize.** **Validate** lists your boards as "Name (id)", prefixed with the environment and sign-in (e.g. "Production, OAuth: …").

OAuth steps 2 and 3 can be skipped by signing in with an access token instead (below).

## Environment and sign-in

The connection has two choices:

| Setting | Options |
|---|---|
| Environment | **Production** (default, `api.pinterest.com`) or **Sandbox** (`api-sandbox.pinterest.com`). Every call (the board list in **Validate**, Create Pin and the metrics read) goes to the chosen host. |
| Sign-in | **OAuth** (default, the Authorize button; tokens renew automatically) or **AccessToken** (a token generated on the app page). |

Existing connections are Production with OAuth, as before.

### Access token

On [My apps](https://developers.pinterest.com/apps/) → your app → **Manage** → **Configure** → **Generate Access Token**, pick the environment and generate a token. Then set **Sign-in** to `AccessToken` and put the token in **Access Token**, best as a secret reference:

```jsonc
{ "Umbraco": { "Automate": { "Secrets": { "PinterestAccessToken": "pina_..." } } } }
```

with `$Umbraco:Automate:Secrets:PinterestAccessToken` in the field. The field is sensitive: encrypted at rest, masked in logs, stripped on export. It's empty by default, because Automate resolves every settings field, and a default reference would fail OAuth connections on sites where that secret isn't set.

- The token is tied to the environment it was generated for: sandbox and production tokens aren't interchangeable.
- It lasts **30 days** and can't be refreshed. When Pinterest answers 401, the step fails as an authentication error saying to generate a new token on the app page.
- No OAuth client id or Authorize step is needed.

### Sandbox

Use Sandbox to try the connector while the app is on **Trial** access: community reports say trial apps get 403 on `POST /v5/pins` in production, and Pinterest says Pins made with trial access are visible only to you in the sandbox. From Pinterest's [Sandbox docs](https://developers.pinterest.com/docs/developer-tools/sandbox/):

- Every call is the production API on the `api-sandbox.pinterest.com` host, and works with Trial or Standard access.
- It needs a sandbox token: Configure tab → Generate Access Token → environment **Sandbox** (30 days).
- Sandbox boards and Pins are separate from production ones: create a board in the sandbox first, and use its id from **Validate**. Pins posted there never appear on your public profile.
- Video Pins can't be created in the sandbox (this package only creates image Pins).
- The sandbox's board list can return an empty page with a bookmark even when boards exist, so **Validate** keeps following the bookmark (250 boards a page, at most 20 pages).

Sandbox needs **Sign-in: AccessToken**. Pinterest's docs show the OAuth token exchange against `api-sandbox.pinterest.com/v5/oauth/token`, but Automate ties a connection type to one OAuth provider (one token endpoint, which is production's), so a Sandbox connection set to OAuth fails **Validate** and its steps with a message saying to use an access token.

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

Output: `results`, one per id in order, each with `id`, `state`, `values` and `reason`. Values use the names Pinterest's `pin_metrics` sends: `impression`, `save`, `pin_click`, `outbound_click` (outbound clicks), `reaction` and `comment`, each when Pinterest sends it as a number. (Pinterest's API description calls outbound clicks `clickthrough`; the live API sends `outbound_click`. `clickthrough` is passed on too, should it ever be sent.) Other outcomes:

- A Pin Pinterest can't find (404) is `gone`.
- A Pin the account can't read (403) is `needsPermission` with reason `scope_missing`.
- A Pin with no lifetime metrics is `unavailable` with reason `unsupported`. Pinterest only has them for video and Idea Pins created before 20 March 2023.
- Any other 4xx for a Pin (except 401, 408 and 429) is `unavailable` with reason `rejected` for that Pin alone; the rest of the batch is still read.
- A 401 fails the step (with an access token, the message says to generate a new one). A 408, 429, 5xx or timeout fails it and Automate retries.

Pins are read one at a time, so a 429 or 5xx part-way through a batch fails the step, and Automate's retry reads the whole batch again.

## Retries

Pinterest has no idempotency key. If the create request fails without a clear answer the step fails **without retrying**; check the board before re-running.
