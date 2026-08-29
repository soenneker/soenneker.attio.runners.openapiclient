[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.Attio.Runners.OpenApiClient/build-and-test.yml?style=for-the-badge)](https://github.com/soenneker/Soenneker.Attio.Runners.OpenApiClient/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.Attio.Runners.OpenApiClient/daily-automatic-update.yml?style=for-the-badge&label=Daily%20Update)](https://github.com/soenneker/Soenneker.Attio.Runners.OpenApiClient/actions/workflows/daily-automatic-update.yml)

# Soenneker.Attio.Runners.OpenApiClient

An automation executable that regenerates `Soenneker.Attio.OpenApiClient` from Attio's OpenAPI documents, validates the generated project, and pushes successful changes to its repository.

This is a repository-maintenance tool, not an application library or NuGet client.

## What a run changes

The runner:

1. Clones the generated-client repository into a temporary working directory.
2. Downloads Attio's core API, standard-object, and webhook OpenAPI documents.
3. Merges paths, webhooks, components, and tags, rejecting conflicting definitions.
4. Applies compatibility fixes to the merged specification.
5. Removes the previous generated sources, preserving the project file.
6. Runs Kiota generation, package restore, and a Release build.
7. Commits and pushes the result with the message `Automated update` only after validation succeeds.

The runner has no dry-run mode. Use credentials for an account that is allowed to push to the generated-client repository.

## Required environment variables

```text
ASPNETCORE_ENVIRONMENT=Development
GH__TOKEN=<GitHub token used to clone and push>
GIT__NAME=<commit author name>
GIT__EMAIL=<commit author email>
```

`ASPNETCORE_ENVIRONMENT` must map to a supported deploy-environment name. The three Git values are read when the successful result is ready to commit.

## OpenAPI source configuration

| Configuration key | Default |
| --- | --- |
| `Attio:CoreOpenApiUrl` | `https://api.attio.com/openapi/api` |
| `Attio:StandardObjectsOpenApiUrl` | `https://api.attio.com/openapi/standard-objects` |
| `Attio:WebhooksOpenApiUrl` | `https://api.attio.com/openapi/webhooks` |

`Attio:ClientGenerationUrl` is retained as a fallback for the core document when `Attio:CoreOpenApiUrl` is not set. Environment-variable configuration uses double underscores, such as `Attio__CoreOpenApiUrl`.

## Run locally

```bash
dotnet run --project src/Soenneker.Attio.Runners.OpenApiClient
```

Run it from a clean, trusted environment with network access, Git, and the .NET SDK available. Kiota is installed or updated as part of the workflow.

## Failure and cancellation behavior

- A download, merge, generation, restore, or build failure prevents the final commit and push.
- Conflicting schemas or other duplicate OpenAPI definitions fail the merge instead of choosing one silently.
- `Ctrl+C` requests cancellation, but cancellation does not roll back filesystem or remote operations that already completed.
- Temporary working files are implementation details; do not treat them as a recoverable copy of the generated repository.
