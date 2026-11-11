# UmbHost.Automate.LinkedIn

LinkedIn connection (OAuth) and **Create LinkedIn Company Post** and **Get LinkedIn Post Metrics** actions for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate). Posts as a company page, not a personal profile.

Requires Umbraco Automate 17.4+ / 18.4+, `Umbraco.Automate.OpenIddict`, and Umbraco CMS 17.6+.

## Setup

1. Create an app at [linkedin.com/developers](https://www.linkedin.com/developers/apps), associated with the company page.
2. On **Products**, request **Community Management API** (gives `w_organization_social` / `r_organization_social`; LinkedIn reviews this, which can take days). Also add **Sign In with LinkedIn using OpenID Connect**, which OpenIddict's LinkedIn provider uses to identify the account.
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

   The package adds the `w_organization_social` and `r_organization_social` scopes itself. Without a `ClientId` the provider is skipped and everything else still loads.
5. **Automate → Connections → Create → LinkedIn → Authorize**, signed in as a member with the Administrator or Content admin role on the page.

### Token lifetime

LinkedIn access tokens last **60 days**, and LinkedIn only issues refresh tokens to approved partners. When the token expires, runs fail with an Authentication error asking you to re-authorise the connection.

### API version

LinkedIn requires a `LinkedIn-Version` header (YYYYMM) and retires each version after about a year. The connection's **API Version** setting defaults to `202610`; move it forward when LinkedIn announces a sunset.

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

**Off until LinkedIn approves the app.** Share statistics need the `rw_organization_admin` permission, and the member who authorises the connection must be an **Administrator** of the page. It's part of the Community Management API, which you already need for posting, but the Standard tier review asks for a screencast of each use case you request, so include **Page Analytics** showing post performance. Once approved, turn the metrics on:

```jsonc
{ "Umbraco": { "Automate": { "Providers": { "LinkedIn": { "Insights": true } } } } }
```

Only `true` (in any case) turns it on; anything else, or no value, leaves it off. While it's off the action sends nothing to LinkedIn and returns every id as `needsPermission` with reason `insights_off`. Turning it on adds `rw_organization_admin` to the scopes the connection asks for, so **re-authorise existing connections**. Until you do, LinkedIn answers 403 and posts come back `needsPermission` with reason `scope_missing`.

Things to know:

- LinkedIn leaves out posts nobody has seen or acted on, and says to treat them as zero. A **deleted post can't be told apart**: it also comes back `ok` with zeroes.
- Statistics are organic only and cover a rolling 12 months.
- LinkedIn's data storage terms allow keeping these figures for one year.
- Not confirmed in LinkedIn's docs:
  - whether `r_organization_admin` alone would be enough. The share statistics page lists only `rw_organization_admin`, so that's what the package asks for.
  - how many posts one call takes. The package sends at most 20 URNs per call.
  - the period of the Development tier's limits (500 requests per app, 100 per member).
