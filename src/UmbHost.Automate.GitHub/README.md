# UmbHost.Automate.GitHub

GitHub connection, actions, a `jsonpath` binding filter and an `X-Hub-Signature-256` webhook
authenticator for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

## What it adds

- **`jsonpath` binding filter** — extract a field from a JSON string: `${ trigger.body | jsonpath:issue.number }`. Automate's built-in Webhook trigger exposes the request body as a *raw string* and the binding engine can't navigate a JSON string on its own — this fills that gap (works on any HTTP response body too).
- **GitHub webhook authenticator** — validates GitHub's `X-Hub-Signature-256` HMAC signature. Select it on the built-in **Webhook** trigger.
- **GitHub OAuth connection** — via OpenIddict WebIntegration. Registered only when a client id is configured, so the site still boots without GitHub credentials.
- **Actions** — **Rename Issue Title**, **Add Issue Comment**, **Set Issue State** (open/closed).

## Requirements

- Umbraco CMS 17+ with [Umbraco.Automate](https://www.nuget.org/packages/Umbraco.Automate) installed.
- A **GitHub OAuth App** (for the connection / write-back actions).

## Install

```bash
dotnet add package UmbHost.Automate.GitHub
```

## Configure the OAuth app

Create a GitHub OAuth App (GitHub → *Settings → Developer settings → OAuth Apps → New*, or under your organisation). Set the **Authorization callback URL** to:

```
{your-site}/umbraco/automate/oauth/callback/github
```

Then add the credentials to config (user secrets in dev, env vars / `appsettings.json` on servers):

```jsonc
{
  "Umbraco": {
    "Automate": {
      "Providers": {
        "GitHub": {
          "ClientId": "your-github-oauth-client-id",
          "ClientSecret": "your-github-oauth-client-secret",
          "Scopes": [ "repo" ]
        }
      }
    }
  }
}
```

`repo` scope allows editing issue titles/comments/state (`public_repo` for public repos only). Without a `ClientId` the GitHub provider is skipped and the connection can't authenticate — everything else still loads.

Create the connection in the backoffice: **Automate → Connections → Create → GitHub → Authorize** (OAuth popup) → **Validate** (calls `GET https://api.github.com/user`).

## The `jsonpath` filter

Usage: `${ <source> | jsonpath:<dotted.path> }`. Supports nested keys and array indices, e.g. `issue.number`, `repository.full_name`, `commits[0].id`. Returns empty when the path is missing or the source isn't JSON. Accepts a raw JSON string, a `JsonElement`, or a rehydrated dictionary — so it works both on `trigger.body` and on the response body of an HTTP-request step.

## Actions

All actions require a GitHub connection.

- **Rename GitHub Issue Title** (`github.renameIssueTitle`) — settings: **Repository** (`owner/repo`), **Issue Number**, **New Title** (all support bindings). Output: `issueNumber`, `htmlUrl`.
- **Add GitHub Issue Comment** (`github.addIssueComment`) — settings: **Repository**, **Issue Number**, **Body**. Output: `commentId`, `htmlUrl`.
- **Set GitHub Issue State** (`github.setIssueState`) — settings: **Repository**, **Issue Number**, **State** (`open`/`closed`). Output: `state`.

## Worked example — GitHub issue → Teamwork task (rebuilds the Zapier zap)

Pair this with [`UmbHost.Automate.Teamwork`](https://www.nuget.org/packages/UmbHost.Automate.Teamwork).

1. **Trigger → Webhook.** Method `POST`; **Authenticator = GitHub HMAC-SHA256 Signature**; set a **Secret**. Save, then copy the webhook URL — it's `{your-site}/automate/webhook/{automationId}`.
2. **Condition (If)** — only act on newly opened issues:
   - `${ trigger.body | jsonpath:action }` **equals** `opened`
3. **Teamwork → Create Task** (inside the true branch):
   - **Tasklist ID** — your tasklist
   - **Name** — `${ trigger.body | jsonpath:issue.number } - ${ trigger.body | jsonpath:issue.title }`
   - **Description** — `${ trigger.body | jsonpath:issue.body }` `` — GitHub: ${ trigger.body | jsonpath:issue.html_url } · Opened by @${ trigger.body | jsonpath:sender.login }``
4. **GitHub → Rename Issue Title:**
   - **Repository** — `${ trigger.body | jsonpath:repository.full_name }`
   - **Issue Number** — `${ trigger.body | jsonpath:issue.number }`
   - **New Title** — `${ previous.taskId } - ${ trigger.body | jsonpath:issue.title }`
5. **Publish** the automation (the webhook endpoint returns 409 until it's Published).

Mirror comments and open/closed state with sibling automations on the same webhook, gated on `${ trigger.body | jsonpath:action }` = `created` / `closed` / `reopened`, using **Add Issue/Task Comment** and **Set Issue/Task Status**.

### Wire up the GitHub webhook

On the repo: **Settings → Webhooks → Add webhook.** Payload URL = the automation's webhook URL (must be publicly reachable — use a tunnel for local dev); Content type `application/json`; **Secret** = the same one on the trigger; Events = **Issues** (+ *Issue comments* / *Pull requests* for mirroring).

## License

[MIT](https://github.com/UmbHost/UmbHost.Automate/blob/main/LICENSE).
