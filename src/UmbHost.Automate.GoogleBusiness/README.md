# UmbHost.Automate.GoogleBusiness

Google Business Profile connection (OAuth) and a **Create Google Business Update** action for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

Requires Umbraco Automate 17.5+ / 18.5+, `Umbraco.Automate.OpenIddict` 17.1.5+, and Umbraco CMS 17.6+.

## Testing status

This package is tested against mocked Google Business Profile API responses only, built from Google's published API reference. It has not been run against the live API.

## Setup

1. In the [Google Cloud console](https://console.cloud.google.com/), create a project and request access to the Business Profile APIs with Google's [access request form](https://developers.google.com/my-business/content/prereqs). Google reviews requests, which can take around 14 days.
2. Once approved, enable the **Google My Business API**, **My Business Account Management API**, **My Business Business Information API** and **My Business Verifications API**.
3. Create an OAuth client (Web application) with the redirect URI `https://<your-site>/umbraco/automate/oauth/callback/googlebusiness`.
4. Config, under the provider name **GoogleBusiness**:

   ```jsonc
   {
     "Umbraco": {
       "Automate": {
         "Providers": {
           "GoogleBusiness": { "ClientId": "...", "ClientSecret": "..." }
         }
       }
     }
   }
   ```

   The package requests the `business.manage` scope with offline access, so tokens renew automatically. Without a `ClientId` the provider is skipped and everything else still loads.
5. **Automate → Connections → Create → Google Business Profile → Authorize.** **Validate** lists your locations as "Title (accounts/…/locations/…)".

## Create Google Business Update

| Setting | Notes |
|---|---|
| Location | `accounts/<id>/locations/<id>`, from **Validate**. |
| Summary | Up to 1,500 characters. Google doesn't state a limit; 1,500 is the widely reported one, not yet confirmed against the live API. |
| Link URL | Optional. Becomes the update's button. |
| Button | `BOOK`, `ORDER`, `SHOP`, `SIGN_UP`, `LEARN_MORE` (the default) or `CALL` (calls the business's phone number; no link needed). |
| Image URL | Optional public https URL of a JPEG or PNG; Google fetches it. |

A location that isn't verified can't post: the step fails with `google_business.location_unverified` before anything is sent.

Output: `postId`, `postUrl` (the update in Google Search).

## Retries

Google has no idempotency key for updates. If the create request fails without a clear answer the step fails **without retrying**; check the profile before re-running.
