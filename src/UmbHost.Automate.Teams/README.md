# UmbHost.Automate.Teams

Microsoft Teams connection and a **Post a Card to Microsoft Teams** action for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate). It posts to a Teams channel or chat through a Teams Workflows webhook.

Requires Umbraco Automate 17.4+ / 18.4+ and Umbraco CMS 17.6+.

## Setup

1. In Teams, make a Workflow from the template **Post to a channel when a webhook request is received** (or the chat version). Copy the address it gives you.
2. The address is a secret: anyone who has it can post. Put it in config:

   ```jsonc
   { "Umbraco": { "Automate": { "Secrets": { "TeamsWebhookUrl": "..." } } } }
   ```

   Environment variable form: `Umbraco__Automate__Secrets__TeamsWebhookUrl`.
3. **Automate → Connections → Create → Microsoft Teams.** Press **Test**: it posts one test card to the channel or chat.

The address must be an `https://` address on a Microsoft Workflows host (`*.logic.azure.com` or `*.api.powerplatform.com`). A redirect is never followed.

## Post a Card to Microsoft Teams

| Setting | Notes |
|---|---|
| Text | Shown exactly as written; Markdown and HTML characters are not interpreted. Up to 4,000 characters. |
| Image URL | Optional https URL. Teams loads the image from that address, so it only appears if your site can be reached from the internet. |
| Image Alt Text | Describes the image for screen readers. |
| Link URL | Optional https URL, shown as a button. |
| Link Title | The button's label. |

Output: `accepted`, `note`. Teams gives no post id or address, so there is no post URL. A card may be at most 28,000 bytes; a larger one is refused before anything is sent.

## Retries

Teams has no idempotency key. A 429 is retried. A 5xx, a timeout or a network error fails **without** retrying and says the post may exist, because a retry could post twice. A 400 or 404 usually means the Workflow was deleted or switched off.

Failures name the host and the status only. Teams' own error text can contain the Workflow's id, which is part of the secret address, so it is never put in a message or log.
