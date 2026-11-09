# UmbHost.Automate.Meta

Facebook &amp; Instagram connection and two actions for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate):

- **Create Facebook Page Post**
- **Create Instagram Post**, to the Instagram professional account linked to the same Page

Requires Umbraco Automate 17.4+ / 18.4+ and Umbraco CMS 17.6+.

## Setup

1. Create a Meta app (type **Business**) and get it through App Review for `pages_manage_posts`, `pages_read_engagement`, `instagram_basic` and `instagram_content_publish`. The Instagram account must be a professional (Business or Creator) account linked to the Page.
2. Get a Page access token that doesn't expire:
   1. Exchange a short-lived user token for a long-lived one:
      `GET https://graph.facebook.com/v27.0/oauth/access_token?grant_type=fb_exchange_token&client_id=…&client_secret=…&fb_exchange_token=…`
   2. Call `GET /me/accounts` with that long-lived token. The Page's `access_token` has no expiry date. It stops working only if it's revoked, for example by a password change or the app losing its permissions.
3. Config:

   ```jsonc
   { "Umbraco": { "Automate": { "Secrets": { "MetaPageAccessToken": "..." } } } }
   ```

4. **Automate → Connections → Create → Facebook & Instagram.** Enter the Page ID and run **Validate**. It reports the linked Instagram account id; enter that in **Instagram Account ID**.

The **Graph API Version** setting defaults to `v27.0`. Meta supports each version for about two years.

## Create Facebook Page Post

| Setting | Notes |
|---|---|
| Message | Post text. |
| Link URL | Optional. Facebook builds the preview card itself. Ignored when an image is set. |
| Image URL | Optional public https URL. Facebook downloads it. |

## Create Instagram Post

| Setting | Notes |
|---|---|
| Caption | Links in captions aren't clickable on Instagram. |
| Image URL | **Required.** Public https URL of a **JPEG**. Instagram downloads it. |
| Image Alt Text | Optional. |

The action creates a media container, waits (up to two minutes) for Instagram to process the image, then publishes it. Instagram allows 100 API-published posts per account per 24 hours.

Both actions output `postId` and `postUrl`.

## Retries

The Graph API has no idempotency key. Instagram's container steps are safe to retry because nothing is public until the publish call. If the publish or Facebook post request fails without a clear answer, the step fails **without retrying**; check the account before re-running.
