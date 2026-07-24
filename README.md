# UmbHost.Automate

Community provider packages for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

| Package | What it adds |
|---|---|
| [`UmbHost.Automate.Teamwork`](src/UmbHost.Automate.Teamwork) | Teamwork Projects connection + Create Task / Add Comment / Set Status actions |
| [`UmbHost.Automate.GitHub`](src/UmbHost.Automate.GitHub) | GitHub OAuth connection + Rename Title / Add Comment / Set State actions, a `jsonpath` binding filter, and an `X-Hub-Signature-256` webhook authenticator |

Together they rebuild a GitHub↔Teamwork integration (create a Teamwork task from a new GitHub
issue/PR, write the task id back into the title, mirror comments and open/closed state) as no-code
Umbraco Automate workflows — see the [GitHub package README](src/UmbHost.Automate.GitHub/README.md)
for the worked example.

## Why the GitHub package ships a filter + authenticator (not a trigger)

Umbraco Automate has no custom typed webhook trigger — the built-in Webhook trigger exposes the
request body as a raw JSON string, and the binding engine can't navigate a JSON string on its own.
So GitHub webhooks are consumed with the **built-in Webhook trigger** plus this package's `jsonpath`
filter (`${ trigger.body | jsonpath:issue.number }`) and a GitHub `X-Hub-Signature-256` authenticator.

## Build & test

```bash
dotnet test UmbHost.Automate.slnx
```

## Release

Push a `v*.*.*` tag. The [Release workflow](.github/workflows/release.yml) packs both packages and
publishes to NuGet.org (via **Trusted Publishing** / OIDC — no stored API key) and GitHub Packages,
then cuts a GitHub Release. One-time setup: add a NuGet Trusted Publishing policy for each package id
bound to this repo + `release.yml`, and set the `NUGET_USER` repository variable.

## License

MIT.
