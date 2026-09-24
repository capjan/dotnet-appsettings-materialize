# Contributing

## Development prerequisites

- .NET SDK 10.0.203 or a compatible .NET 10 SDK
- Linux, macOS, or Windows

## Build and test

```bash
dotnet restore
dotnet build AppSettings.Materializer.sln --no-restore
dotnet test AppSettings.Materializer.sln --no-restore -m:1
```

The serial test option keeps the local VSTest host reliable in restricted environments.

Changes to merge semantics should add a roundtrip test that compares the original provider chain with the generated file. Do not add tests that log configuration values.

## Packaging

```bash
dotnet pack src/AppSettings.Materializer.Cli/AppSettings.Materializer.Cli.csproj \
  -c Release -p:Version=1.0.0 -o ./artifacts
```

Use an explicit version for every package. Keep the CLI package as the only global-tool package and keep the core library independently reusable.
