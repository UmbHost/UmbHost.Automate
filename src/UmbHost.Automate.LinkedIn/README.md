# UmbHost.Automate.LinkedIn

Two LinkedIn connections (OAuth) for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate): **LinkedIn** posts as a company page (**Create LinkedIn Company Post**, **Get LinkedIn Post Metrics**), and **LinkedIn profile** posts as you, on your own profile (**Create LinkedIn Profile Post**). Each needs its own LinkedIn app, and a site can use both at once.

Requires Umbraco Automate 17.4+ / 18.4+, `Umbraco.Automate.OpenIddict`, and Umbraco CMS 17.6+.

## Setup: company page

1. Create an app at [linkedin.com/developers](https://www.linkedin.com/developers/apps), associated with the company page.
2. On **Products**, request **Community Management API** (gives `w_organization_social` / `r_organization_social`; LinkedIn reviews this, which can take days). It must be the **only** product on the app: LinkedIn refuses to add it to an app that has any other, so use a new app if yours already has one (Sign In with LinkedIn, Share on LinkedIn and so on). The connection doesn't need Sign In with LinkedIn: it never asks for `openid` or `profile`.
3. On **Auth**, add the redirect URL `https://<your-site>/umbraco/automate/oauth/callback/linkedin`.
4. Config:

   ```jsonc
   {
     "Umbraco": {
       "Automate": {
         "Providers": {
           "LinkedIn": { "ClientId": "...", "ClientSecret": "..." }
         }
       }
     }
   }
   ```

   The package adds the `w_organization_social`, `r_organization_social` and `rw_organization_admin` scopes itself, whatever the Insights switch says. Without a `ClientId` the provider is skipped and everything else still loads.
5. **Automate → Connections → Create → LinkedIn → Authorize**, signed in as a member with the **Administrator** role on the page. The connection asks for `rw_organization_admin`, which LinkedIn restricts to page Administrators, so a Content admin can't connect.
6. **Test connection** lists the company pages the account can post as, `Name (id)`, so you can copy the organization id from there. If LinkedIn refuses to list them (403), the test warns: re-authorise as a page Administrator. The id is also in the page's admin URL (`linkedin.com/company/<id>/admin`).

### Sign-in

The package registers LinkedIn with OpenIddict as a plain OAuth 2.0 provider, following LinkedIn's [Authorization Code Flow](https://learn.microsoft.com/en-us/linkedin/shared/authentication/authorization-code-flow): `https://www.linkedin.com/oauth/v2/authorization`, then `https://www.linkedin.com/oauth/v2/accessToken` with the client id and secret in the request body. There's no userinfo call and no PKCE (LinkedIn's standard flow has none; its PKCE flow is for native apps). The callback stays `/umbraco/automate/oauth/callback/linkedin`, so existing connections keep working.

### Token lifetime

LinkedIn access tokens last **60 days**, and LinkedIn only issues refresh tokens to approved partners. When the token expires, runs fail with an Authentication error asking you to re-authorise the connection.

### API version

LinkedIn requires a `LinkedIn-Version` header (YYYYMM). It [publishes a version every month and supports each for a minimum of one year](https://learn.microsoft.com/en-us/linkedin/marketing/versioning), then retires it. Both connections (company page and profile) have an **API Version** setting:

- **Leave it empty** (the default) to use the connector's current version, `202610` in this release. It moves forward when you update the package, so an empty setting never goes stale on its own.
- **Or enter a version** such as `202610` to pin it. It must be YYYYMM with a month from 01 to 12, and not after the current month. A pinned version stays as you set it, including connections saved by earlier releases (which stored the default of the day): moving it on is up to you.
- **Test connection** checks the form first (Automate's settings validation runs there, because Test connection saves and then resolves the settings). It warns, while still connecting, once the version is more than 10 months old, and suggests a newer one.
- When LinkedIn refuses the version, it answers `426` with `"code": "NONEXISTENT_VERSION"` and "Requested version … is not active" ([error responses, example 5](https://learn.microsoft.com/en-us/linkedin/marketing/error-responses)). The step then fails as a **Validation** error, not retried, saying "LinkedIn no longer supports API version 202410. Set a newer version in the connection's API Version." A missing version (`400 VERSION_MISSING`) reads the same way.

## Setup: your own profile

LinkedIn keeps the Community Management API alone on its app, so posting as yourself needs a **second app**.

1. Create another app at [linkedin.com/developers](https://www.linkedin.com/developers/apps) (LinkedIn asks every app for a company page; any page you administer will do).
2. On **Products**, add **Share on LinkedIn** (`w_member_social`) and **Sign In with LinkedIn using OpenID Connect** (`openid`, `profile`). Both are self-serve, with no review.
3. On **Auth**, add the redirect URL `https://<your-site>/umbraco/automate/oauth/callback/linkedinprofile`.
4. Config, beside the company page's:

   ```jsonc
   {
     "Umbraco": {
       "Automate": {
         "Providers": {
           "LinkedInProfile": { "ClientId": "...", "ClientSecret": "..." }
         }
       }
     }
   }
   ```

   This one uses OpenIddict's own LinkedIn provider under the name `LinkedInProfile`, which asks for `openid` and `profile`; the package adds `w_member_social`. Without a `ClientId` it is skipped.
5. **Automate → Connections → Create → LinkedIn profile → Authorize**, signed in as the member whose profile the posts go to.
6. **Test connection** says who it is connected as.

The token lasts 60 days, with no refresh, as for the company page.

## Create LinkedIn Profile Post

Posts as the connected member: the action asks LinkedIn who the account is (`GET /v2/userinfo`) and posts as `urn:li:person:{sub}` through the same [Posts API](https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/posts-api) as the company page (`w_member_social`). The settings are the company post's, without the organization id: text, link card (title required), or one image with alt text, and a first comment.

- **Images:** a member's token can't read an image's processing status (LinkedIn: "tokens with only w_member_social permissions would be unable to perform a GET call for rest/images"), so when LinkedIn refuses the check the action waits a moment and posts.
- **First comment** is best effort: LinkedIn's Posts API says `w_member_social` covers comments, its Comments API lists `w_member_social_feed`. If LinkedIn refuses it, the post stays published and the output carries a warning.
- **No figures:** reading a member's post statistics needs `r_member_social`, which LinkedIn restricts to approved developers.

Output: `postUrn`, `postUrl`, `warning`.

## Create LinkedIn Company Post

| Setting | Notes |
|---|---|
| Organization ID | The page's numeric id (`linkedin.com/company/<id>/admin`). |
| Text | `#hashtags` are kept. Other characters LinkedIn reserves (`( ) [ ] { } < > @ * _ ~ \|`) are escaped for you. |
| Link URL / Title / Description | Optional link card. LinkedIn doesn't fetch the page, so a title is required. |
| Image URL / Alt Text | Optional JPEG, PNG or GIF. A post has a link card **or** an image, not both. |

Output: `postUrn`, `postUrl`.

## Retries

LinkedIn has no idempotency key. If the post request fails without a clear answer the step fails **without retrying**; check the page before re-running.

## Get LinkedIn Post Metrics

Reads company page posts' lifetime share statistics with `GET /rest/organizationalEntityShareStatistics` ([docs](https://learn.microsoft.com/en-us/linkedin/marketing/community-management/organizations/share-statistics)), under LinkedIn's own names: `impressionCount`, `uniqueImpressionsCount`, `clickCount`, `likeCount`, `commentCount`, `shareCount` and `engagement`.

| Setting | Notes |
|---|---|
| Organization ID | The page's numeric id, the same as on the post step. |
| Ids | JSON array of 1 to 100 post URNs (`urn:li:share:…` or `urn:li:ugcPost:…`), as **Create LinkedIn Company Post** outputs them. Anything else comes back `gone` and is never sent. |

Output: `results`, one per id: `state` (`ok`, `gone`, `unavailable`, `needsPermission`), `values` and `reason`.

**Off until LinkedIn approves the app.** Share statistics need the `rw_organization_admin` permission (the connection always asks for it) and an **Administrator** of the page as the member who authorised the connection. It's part of the Community Management API, which you already need for posting, but the Standard tier review asks for a screencast of each use case you request, so include **Page Analytics** showing post performance. Once approved, turn the metrics on:

```jsonc
{ "Umbraco": { "Automate": { "Providers": { "LinkedIn": { "Insights": true } } } } }
```

Only `true` (in any case, surrounding spaces ignored) turns it on; anything else, or no value, leaves it off. While it's off the action sends nothing to LinkedIn and returns every id as `needsPermission` with reason `insights_off`. The switch only turns these reads on or off; it doesn't change the scopes, so turning it on needs no re-authorisation. A connection authorised before the package asked for `rw_organization_admin` gets a 403, and its posts come back `needsPermission` with reason `scope_missing`, until it's re-authorised.

Things to know:

- LinkedIn leaves out posts nobody has seen or acted on, and says to treat them as zero. A **deleted post can't be told apart**: it also comes back `ok` with zeroes.
- A 403 makes that call's posts `needsPermission` with reason `scope_missing`. Any other 4xx (except 401, 408 and 429) makes them `unavailable` with reason `rejected`, and the rest of the batch is still read. A 401 fails the step; a 408, 429, 5xx or timeout fails it and Automate retries.
- Statistics are organic only and cover a rolling 12 months.
- LinkedIn's data storage terms allow keeping these figures for one year.
- Not confirmed in LinkedIn's docs:
  - whether `r_organization_admin` alone would be enough. The share statistics page lists only `rw_organization_admin`, so that's what the package asks for.
  - how many posts one call takes. The package sends at most 20 URNs per call.
  - the `ugcPosts[0]=…` form for UGC posts, sent with the Rest.li 2.0 header as LinkedIn's sample shows it, hasn't been run against the live API. If LinkedIn refuses it, those posts come back `rejected` and share posts are unaffected.
  - the period of the Development tier's limits (500 requests per app, 100 per member).
