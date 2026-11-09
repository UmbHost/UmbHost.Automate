# UmbHost.Automate.LinkedIn

LinkedIn connection (OAuth) and a **Create LinkedIn Company Post** action for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate). Posts as a company page, not a personal profile.

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
