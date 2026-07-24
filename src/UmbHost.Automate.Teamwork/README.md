# UmbHost.Automate.Teamwork

Teamwork Projects connection type and actions for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

## Overview

Adds Teamwork Projects connectivity to Umbraco Automate: a token-authenticated connection plus
actions you can use as workflow steps — create a task, comment on a task, and complete/reopen a task.

## Features

- **Teamwork connection type** — API-token (HTTP Basic) connection, validated against `me.json`.
- **Create Task** action — creates a task in a tasklist and outputs the created `taskId`.
- **Add Task Comment** action — comments on a task.
- **Set Task Status** action — completes or reopens a task.

## Installation

```bash
dotnet add package UmbHost.Automate.Teamwork
```

## Configuration

Store the token as a secret and reference it from the connection (the default connection value is
`$Umbraco:Automate:Secrets:TeamworkApiToken`):

```json
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

Then create a **Teamwork Projects** connection in a workspace (Site = your subdomain), and reference
it from the Teamwork actions.

## License

MIT.
