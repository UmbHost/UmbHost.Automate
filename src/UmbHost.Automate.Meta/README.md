# UmbHost.Automate.Meta

Facebook &amp; Instagram connection and four actions for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate):

- **Create Facebook Page Post**
- **Create Instagram Post**, to the Instagram professional account linked to the same Page
- **Get Facebook Post Metrics** and **Get Instagram Post Metrics**, off until Meta approves the app (see [Post metrics](#post-metrics))

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

## Post metrics

**Off until Meta approves the app.** One switch covers both networks, because both go through the same Meta App Review:

```jsonc
{ "Umbraco": { "Automate": { "Providers": { "Meta": { "Insights": true } } } } }
```

Only `true` (in any case, surrounding spaces ignored) turns it on; anything else, or no value, leaves it off. While it's off both actions send nothing to Meta and return every id as `needsPermission` with reason `insights_off`.

The Page token has to carry these permissions. Each needs App Review with a screencast of the metrics shown in your site, and Advanced Access needs Business Verification ([permissions](https://developers.facebook.com/docs/permissions)):

| Permission | For |
|---|---|
| `read_insights` | Facebook views. Without it Meta returns an empty dataset, not an error. |
| `pages_read_engagement` | Facebook counts and views; Instagram insights. |
| `pages_read_user_content` | Facebook post counts (from Meta's permissions reference; the post and reactions pages name only `pages_read_engagement`, so this is unconfirmed). |
| `instagram_manage_insights` | Instagram insights (with `instagram_basic`, plus `ads_management` and `ads_read` if you have a Business Manager role on the Page). |

The Page token is pasted in, so it carries whatever was granted when it was generated. While the switch is on, **Validate** checks the token and lists any of these four it lacks as a warning, never a failure (`instagram_manage_insights` only when an Instagram account is linked); generate a new Page token with them. Validate reads `/me/permissions` with the Page token. Meta documents that call for user ids only, so if it can't be read, Validate warns that the permissions couldn't be checked. While the switch is off, Validate doesn't check them.

### Get Facebook Post Metrics

Two calls per post: `GET /{post-id}?fields=shares,comments.summary(true).limit(0),reactions.summary(true).limit(0)` for `reactions`, `comments` and `shares`, and `GET /{post-id}/insights?metric=post_media_view` for `post_media_view` (views, Meta's replacement for the retired `post_impressions`). A post is `ok` only when both succeed. If the views come back empty (no `read_insights`), the post is `needsPermission` with reason `scope_missing`.

Ids: JSON array of 1 to 100 `{page-id}_{post-id}` ids, as **Create Facebook Page Post** outputs them. Anything else comes back `gone` and is never sent.

- Clicks aren't offered: `post_clicks` no longer appears in Meta's Page insights docs.
- `post_media_view` is named as the replacement for `post_impressions` on Meta's deprecations page, but the current Page insights reference doesn't list it as a post metric, so that it works per post is unconfirmed.
- Not confirmed in Meta's docs: whether a post nobody has shared leaves `shares` out (it's then left out, not reported as 0), and which error marks a deleted post (the package uses code 100 with subcode 33, or a 404).

### Get Instagram Post Metrics

One call per post: `GET /{ig-media-id}/insights?metric=views,reach,likes,comments,shares,saved,reposts&period=lifetime` ([docs](https://developers.facebook.com/docs/instagram-platform/reference/instagram-media/insights)), under Instagram's own names.

Ids: JSON array of 1 to 100 Instagram media ids, as **Create Instagram Post** outputs them. Anything else comes back `gone` and is never sent.

- Albums have no insights and come back `unavailable` with reason `unsupported`. Meta doesn't document what the insights call returns for an album, so when it's refused with an unexpected 4xx the package looks up the post's `media_type` and treats only a `CAROUSEL_ALBUM` that way. Any other post, or one whose lookup is also refused, is `unavailable` with reason `rejected`. A lookup that is throttled or unauthorised fails the step as such.
- An answer with no metrics at all is treated as `needsPermission` with reason `scope_missing`. Meta says only that unavailable data "returns empty", so reading that as a missing permission is unconfirmed.
- Figures can lag by up to 48 hours.

Both actions output `results`, one per id: `state` (`ok`, `gone`, `unavailable`, `needsPermission`), `values` and `reason`. Any 4xx for a post that isn't a missing permission, a missing post, 401, 408 or 429 is `unavailable` with reason `rejected` for that post alone, and the rest of the batch is still read. Graph throttling (codes 4 and 17; 32 and 613 are assumed from Meta's rate-limit guide, not confirmed) fails the step so Automate retries it, and an expired token (code 190) fails it as an authentication error.
