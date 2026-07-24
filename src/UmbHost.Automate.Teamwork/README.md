# UmbHost.Automate.Teamwork

Teamwork Projects connection type and actions for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

## What it adds

- **Teamwork Projects connection** — API-token authenticated (HTTP Basic), validated against `me.json`.
- **Create Task** action → outputs the created `taskId` (and `taskUrl`) for use in later steps.
- **Add Task Comment** action.
- **Set Task Status** action (complete / reopen).

## Requirements

- Umbraco CMS 17+ with [Umbraco.Automate](https://www.nuget.org/packages/Umbraco.Automate) installed.
- A Teamwork Projects account and an **API token** (Teamwork → your avatar → *Edit My Details* → *API & Mobile* → *Show your token*, or use a dedicated service-account token).

## Install

```bash
dotnet add package UmbHost.Automate.Teamwork
```

## Configure the token

The connection's **API Token** field defaults to the config reference `$Umbraco:Automate:Secrets:TeamworkApiToken`, so keep the secret out of the connection record and in configuration. In **user secrets** (dev) or environment variables / `appsettings.json`:

```jsonc
{
  "Umbraco": {
    "Automate": {
      "Secrets": {
        "TeamworkApiToken": "your-teamwork-api-token"
      }
    }
  }
}
```

On servers use the env-var form: `Umbraco__Automate__Secrets__TeamworkApiToken`.

> You can also paste the token directly into the connection's API Token field instead of using the config reference — but a secret in config is preferred.

## Create the connection

In the backoffice: **Automate → Connections → Create → Teamwork Projects**. Set:

- **Site** — your Teamwork subdomain only, e.g. `umbhost` for `umbhost.teamwork.com`.
- **API Token** — leave the default `$Umbraco:Automate:Secrets:TeamworkApiToken` (or paste a token).

**Validate** — it calls `GET /projects/api/v3/me.json` and reports the connected site.

## Actions

All actions require a Teamwork connection.

### Create Teamwork Task (`teamwork.createTask`)

| Setting | Notes |
|---|---|
| **Tasklist ID** | The numeric id of the target tasklist. Open the tasklist in Teamwork; the id is in the URL (`.../tasklists/1640553`). |
| **Name** | Task name. Supports `${ }` bindings. |
| **Description** | Optional. Supports bindings. |
| **Priority** | Optional — `low` / `medium` / `high`. |
| **Tags** | Optional — comma-separated tag names. |

**Output:** `taskId` (long), `taskUrl` (string). Reference in later steps as `${ steps.<step>.taskId }` — or, if it's the immediately preceding step, `${ previous.taskId }`.

### Add Teamwork Task Comment (`teamwork.addTaskComment`)

Settings: **Task ID**, **Body** (bindings). Output: `commentId`.

### Set Teamwork Task Status (`teamwork.setTaskStatus`)

Settings: **Task ID**, **Status** (`complete` or `reopen`). Output: `status`.

## Notes

- Base URL is `https://{Site}.teamwork.com/projects/api/v3/`; auth is HTTP Basic with the API token as the username.
- Create Task reads the new id from the v3 response (`{ "task": { "id": … } }`).

## License

[MIT](https://github.com/UmbHost/UmbHost.Automate/blob/main/LICENSE).
