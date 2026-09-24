# dotnet-appsettings-materialize

> **Predictable ASP.NET Core deployments:** turn layered `appsettings` files into the one `appsettings.json` your published app runs.

<img src="assets/hero-materialize-deployment.png" alt="Many ASP.NET Core configuration files are materialized into one appsettings.json and deployed to a running app">

```bash
dotnet tool install --global dotnet-appsettings-materialize
```

`appsettings-materialize` turns a defined sequence of ASP.NET Core JSON configuration layers into one deterministic, deployment-ready file, simplifying administration and removing ambiguity about which file contains the effective settings from that provider chain—all while preserving .NET's configuration semantics.

> [!TIP]
> **The .NET-native advantage:** `appsettings-materialize` uses `Microsoft.Extensions.Configuration.Json` as its source of truth and computes the exact effective result of the selected ASP.NET Core JSON provider chain ahead of deployment. The server can then consume one deterministic file instead of resolving multiple JSON layers at runtime, while provider order, precedence rules, and configuration-key semantics remain unchanged.

## Install

The installed command is `appsettings-materialize`.

## Usage

Pass input files in priority order. The last input has the highest priority:

```bash
appsettings-materialize merge \
  --input appsettings.json \
  --input appsettings.Integration.json \
  --output appsettings.effective.json \
  --pretty
```

For the normal ASP.NET Core file convention:

```bash
appsettings-materialize merge \
  --directory ./publish \
  --environment Integration \
  --output ./publish/appsettings.json \
  --overwrite
```

Useful options are:

- `--overwrite` allows an existing output file to be replaced.
- `--check` validates the result and writes no file.
- `--pretty` enables indented JSON.
- `--no-sort` keeps the discovered property order instead of sorting properties.
- `--verbose` prints file names, key counts, and a SHA-256 hash, never configuration values.

The output is written atomically through a temporary file in the destination directory. Inputs are never deleted automatically. The convention form may intentionally replace the base `appsettings.json` when `--overwrite` is explicit; a later environment input may not be used as the output path.

## Semantics

The tool builds the same provider chain as:

```csharp
new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddJsonFile("appsettings.Integration.json")
    .Build();
```

Later providers override earlier values at the same configuration key. Arrays are represented by numeric configuration segments (`Items:0:Name`), so an override can change one array index while other indexes remain inherited.

The generated file is loaded again through `IConfiguration` and all effective keys and values are compared before it is written. Scalar JSON values retain their effective input type where that type is unambiguous; configuration equality is always checked as string-valued `IConfiguration` data.

`null`, comments, trailing commas, nested objects, arrays, booleans, numbers, Unicode, and empty objects/arrays are supported according to the JSON configuration provider. An array gap cannot be represented deterministically and is rejected.

One important provider edge case is also rejected: if an earlier layer contains `Section:Child` and a later layer contains a scalar at `Section`, `IConfiguration` exposes both the scalar and the inherited child. A single JSON document cannot represent both at that path without changing configuration semantics, so the tool fails instead of silently dropping a key. The same applies to the reverse scalar/object conflict.

## What is and is not supported

Supported:

- materializing explicitly supplied Microsoft.Extensions.Configuration JSON providers
- deterministic nested JSON output
- Linux, Windows, and macOS
- global .NET tool installation
- CI usage with non-secret configuration layers

Not supported by design:

- RFC 7396 JSON Merge Patch
- general-purpose deep merge, array union, or configurable array strategies
- environment-variable export
- command-line argument export
- User Secrets or other secret management
- JSON Schema validation
- modifying a running ASP.NET Core application

Environment variables, User Secrets, and command-line arguments therefore remain external overrides and are not copied into the output file.

## CI examples

GitHub Actions:

```yaml
- name: Install materializer
  run: dotnet tool install --global dotnet-appsettings-materialize --version 1.0.0
- name: Materialize integration settings
  run: >-
    appsettings-materialize merge
    --directory ./publish
    --environment Integration
    --output ./publish/appsettings.json
    --overwrite
```

GitLab CI:

```yaml
materialize:
  image: mcr.microsoft.com/dotnet/sdk:10.0
  script:
    - dotnet tool install --global dotnet-appsettings-materialize --version 1.0.0
    - export PATH="$PATH:$HOME/.dotnet/tools"
    - appsettings-materialize merge --input appsettings.json --input appsettings.Stage.json --output appsettings.effective.json
```

Pin a released version in CI. Do not use an unversioned `latest` package.

## Publish-directory example

After `dotnet publish`, materialize only the files intended for the target environment:

```text
dotnet publish -c Release -o ./publish
appsettings-materialize merge \
  --directory ./publish \
  --environment Stage \
  --output ./publish/appsettings.json \
  --overwrite
```

The tool does not remove `appsettings.Stage.json`; cleanup and packaging remain explicit pipeline steps. Keep `ASPNETCORE_ENVIRONMENT` and environment-variable overrides external to the generated file.

## Security

The output contains the effective values of the input files. Treat it as sensitive whenever an input contains sensitive data. The tool does not discover, redact, or upload secrets, and it does not print configuration values in normal or verbose output. See [SECURITY.md](SECURITY.md) for reporting guidance.

## Release and versioning

The project uses SemVer. A release version is passed explicitly to `dotnet pack`, and CI tests the package as a local global tool before publication. The version must be pinned in deployment scripts and tool manifests.

```bash
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build -m:1
dotnet pack src/AppSettings.Materializer.Cli/AppSettings.Materializer.Cli.csproj \
  -c Release --no-build -p:Version=1.0.0 -o ./artifacts
```

## License

MIT. See [LICENSE](LICENSE).
