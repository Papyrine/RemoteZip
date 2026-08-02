# Contributing

## Requirements

- .NET SDK per [global.json](global.json)

## Build and test

```pwsh
dotnet build src --configuration Release
dotnet run --project src/RemoteZip.Tests --configuration Release --no-build
```

Tests use [TUnit](https://github.com/thomhurst/TUnit) and [Verify](https://github.com/VerifyTests/Verify). On snapshot changes, `*.received.*` files appear next to the tests; accept them via a diff tool ([DiffEngine](https://github.com/VerifyTests/DiffEngine) launches one automatically) or by renaming to `*.verified.*`.

`LiveNugetTests` requires network access to api.nuget.org.

## Docs

`readme.md` and `nuget.md` are maintained by [MarkdownSnippets](https://github.com/SimonCropp/MarkdownSnippets), which runs on test-project builds. Code blocks between snippet markers are pulled from the test sources — edit those, not the readme.
