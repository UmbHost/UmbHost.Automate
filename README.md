# UmbHost.Automate

Community provider packages for [Umbraco Automate](https://github.com/umbraco/Umbraco.Automate).

| Package | What it adds |
|---|---|
| [`UmbHost.Automate.Teamwork`](src/UmbHost.Automate.Teamwork) | Teamwork Projects connection + Create Task / Add Comment / Set Status actions |
| [`UmbHost.Automate.GitHub`](src/UmbHost.Automate.GitHub) | GitHub OAuth connection + Rename Title / Add Comment / Set State actions, a `jsonpath` binding filter, and an `X-Hub-Signature-256` webhook authenticator |
| [`UmbHost.Automate.Bluesky`](src/UmbHost.Automate.Bluesky) | Bluesky app-password connection + Create Post (clickable links, image) |
| [`UmbHost.Automate.Mastodon`](src/UmbHost.Automate.Mastodon) | Mastodon token connection + Create Status (image, idempotent retries) |
| [`UmbHost.Automate.LinkedIn`](src/UmbHost.Automate.LinkedIn) | LinkedIn OAuth connection + Create Company Post (link card or image) |
| [`UmbHost.Automate.X`](src/UmbHost.Automate.X) | X (Twitter) OAuth 2.0 connection + Create Post (image, alt text) |
| [`UmbHost.Automate.Meta`](src/UmbHost.Automate.Meta) | Facebook Page token connection + Create Facebook Page Post / Create Instagram Post |

Together they rebuild a GitHub↔Teamwork integration (create a Teamwork task from a new GitHub
issue/PR, write the task id back into the title, mirror comments and open/closed state) as no-code
Umbraco Automate workflows — see the [GitHub package README](src/UmbHost.Automate.GitHub/README.md)
for the worked example.

## Why the GitHub package ships a filter + authenticator (not a trigger)

Umbraco Automate has no custom typed webhook trigger — the built-in Webhook trigger exposes the
request body as a raw JSON string, and the binding engine can't navigate a JSON string on its own.
So GitHub webhooks are consumed with the **built-in Webhook trigger** plus this package's `jsonpath`
filter (`${ trigger.body | jsonpath:issue.number }`) and a GitHub `X-Hub-Signature-256` authenticator.

## Social packages: retries never double-post

Automate retries a failed step (10 times by default) unless the failure is in a terminal category.
For the social post actions a retry is a duplicate public post, so they share one rule
(`src/Shared/PostOutcome.cs`): sign-in and media uploads retry as normal, a definite rejection
(4xx) fails without retry, a 429 retries, and a create-post request with no clear answer (5xx,
timeout) fails **without** retrying and says the post may exist. Mastodon is the exception: it
honours an idempotency key, so it retries safely. Images are downloaded through Automate's
SSRF-protected HTTP client (`src/Shared/ImageFetcher.cs`), which is why the social packages need
Automate 17.4+ / 18.4+.

## Build & test

```bash
dotnet test UmbHost.Automate.slnx
```

## Release

Push a `v*.*.*` tag. The [Release workflow](.github/workflows/release.yml) packs every package and
publishes to NuGet.org (via **Trusted Publishing** / OIDC — no stored API key) and GitHub Packages,
then cuts a GitHub Release. One-time setup: add a NuGet Trusted Publishing policy for each package id
bound to this repo + `release.yml`, and set the `NUGET_USER` repository variable.

## License

MIT.
