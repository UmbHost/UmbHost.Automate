# UmbHost.Automate.GitHub

GitHub connection, actions, a `jsonpath` binding filter and an `X-Hub-Signature-256` webhook
authenticator for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

## Overview

Umbraco Automate's built-in webhook trigger exposes the request body as a raw JSON string, and the
binding engine can't navigate a JSON string on its own. This package makes GitHub webhooks usable and
adds write-back actions:

- **`jsonpath` binding filter** — `${ trigger.body | jsonpath:issue.number }`, `| jsonpath:repository.full_name }`, etc. Also works on any HTTP response body.
- **GitHub webhook authenticator** — validates GitHub's `X-Hub-Signature-256` HMAC signature (select it on the built-in Webhook trigger).
- **GitHub OAuth connection** — via OpenIddict WebIntegration.
- **Actions** — Rename Issue Title, Add Issue Comment, Set Issue State (open/closed).

## Installation

```bash
dotnet add package UmbHost.Automate.GitHub
```

## Configuration

Create a [GitHub OAuth app](https://github.com/settings/developers) and configure it:

```json
{
  "Umbraco": {
    "Automate": {
      "Providers": {
        "GitHub": {
          "ClientId": "your-github-oauth-client-id",
          "ClientSecret": "your-github-oauth-client-secret"
        }
      }
    }
  }
}
```

The OAuth callback URI follows the convention `{your-site}/umbraco/automate/oauth/callback/github` —
add it to your GitHub OAuth app's Authorization callback URL.

## Example: rebuild the GitHub → Teamwork zap

1. **Webhook** trigger (POST) with the **GitHub HMAC-SHA256** authenticator + your webhook secret.
2. Condition: `${ trigger.body | jsonpath:action }` equals `opened`.
3. **Teamwork → Create Task** (from `UmbHost.Automate.Teamwork`): Name `${ trigger.body | jsonpath:issue.number } - ${ trigger.body | jsonpath:issue.title }`.
4. **GitHub → Rename Issue Title**: Repository `${ trigger.body | jsonpath:repository.full_name }`, Issue Number `${ trigger.body | jsonpath:issue.number }`, New Title `${ steps.createTask.taskId } - ${ trigger.body | jsonpath:issue.title }`.

Point a GitHub repository webhook (Issues events, the same secret) at the automation's webhook URL.

## License

MIT.
