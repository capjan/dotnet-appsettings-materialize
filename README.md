# dotnet-appsettings-materialize

> Merge layered ASP.NET Core `appsettings` files into the `appsettings.json` you deploy with your application.

<img src="assets/hero-materialize-deployment.png" alt="Several ASP.NET Core configuration files are combined into one appsettings.json file">

```bash
dotnet tool install --global dotnet-appsettings-materialize
```

`appsettings-materialize` takes a specific sequence of ASP.NET Core JSON configuration files and writes the resulting configuration to one file. It follows the same provider order and override rules as ASP.NET Core, so you can see which settings will actually be deployed.

> [!TIP]
> **Why this matches ASP.NET Core:** the merge uses `Microsoft.Extensions.Configuration.Json` rather than a separate JSON merge implementation. The generated file follows the same provider order, precedence rules, and configuration-key semantics as ASP.NET Core at runtime.

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

Useful options include:

- `--overwrite` allows an existing output file to be replaced.
- `--check` validates the result and writes no file.
- `--pretty` enables indented JSON.
- `--no-sort` keeps the discovered property order instead of sorting properties.
- `--verbose` prints file names, key counts, and a SHA-256 hash, never configuration values.

The output is first written to a temporary file on the destination filesystem and then moved into place. This keeps a failed write from leaving a half-written output file behind. On Unix, the temporary file is created with owner-only (`0600`) permissions inside a private temporary directory; an existing file's ACL and mode are applied just before it is replaced. New output files get owner-only (`0600`) permissions on Unix and a private ACL for the current user, SYSTEM, and local administrators on Windows. When an existing file is overwritten, its Unix mode and ACL are kept on macOS and Linux, and its ACL is kept on Windows. Input files are never deleted automatically. With the directory form, `--overwrite` is required if the output is the base `appsettings.json`; an environment-specific input file cannot also be used as the output.

## Semantics

The tool builds the same provider chain as this code:

```csharp
new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddJsonFile("appsettings.Integration.json")
    .Build();
```

At the same configuration key, a later provider wins. Arrays are represented by numeric configuration segments such as `Items:0:Name`. This means one array item can be overridden while the other items continue to come from an earlier file.

Before writing the output, the tool loads it again through `IConfiguration` and compares all effective keys and values with the original provider chain. Scalar JSON values keep their input type when that type is unambiguous. The comparison itself uses the string values exposed by `IConfiguration`.

`null`, comments, trailing commas, nested objects, arrays, booleans, numbers, Unicode, and empty objects or arrays are supported in the same way as by the JSON configuration provider. Array gaps are rejected because they cannot be represented reliably in the generated JSON.

There is one provider edge case worth calling out. Suppose an earlier file contains `Section:Child` and a later file contains a scalar value at `Section`. `IConfiguration` then exposes both the scalar and the inherited child. One JSON document cannot represent both values at that path without changing the configuration, so the tool reports an error instead of silently dropping a key. The reverse conflict, a scalar followed by an object, is handled the same way.

## What is and is not supported

The tool supports:

- materializing explicitly supplied Microsoft.Extensions.Configuration JSON providers
- repeatable nested JSON output
- Linux, Windows, and macOS
- global .NET tool installation
- CI usage with non-secret configuration layers

It does not support:

- RFC 7396 JSON Merge Patch
- general-purpose deep merge, array union, or configurable array strategies
- environment-variable export
- command-line argument export
- User Secrets or other secret management
- JSON Schema validation
- modifying a running ASP.NET Core application

Environment variables, User Secrets, and command-line arguments stay outside the generated file and can still be used as external overrides.

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

Pin a released version in CI instead of using an unversioned `latest` package.

## Publish-directory example

After `dotnet publish`, combine the files intended for the target environment:

```text
dotnet publish -c Release -o ./publish
appsettings-materialize merge \
  --directory ./publish \
  --environment Stage \
  --output ./publish/appsettings.json \
  --overwrite
```

The tool leaves `appsettings.Stage.json` in place. If it should be removed from the package, make that a separate cleanup or packaging step. Keep `ASPNETCORE_ENVIRONMENT` and environment-variable overrides outside the generated file.

## Security

The output contains the effective values from the input files. Treat it as sensitive if any input contains sensitive data. The tool does not find, redact, or upload secrets, and it does not print configuration values in normal or verbose output. See [SECURITY.md](SECURITY.md) if you need to report a security issue.

## Release and versioning

The project follows SemVer. Releases pass the version explicitly to `dotnet pack`, and CI installs the package as a local global tool before it is published. Pin the version in deployment scripts and tool manifests.

```bash
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build -m:1
dotnet pack src/AppSettings.Materializer.Cli/AppSettings.Materializer.Cli.csproj \
  -c Release --no-build -p:Version=1.0.0 -o ./artifacts
```

## License

MIT. See [LICENSE](LICENSE).
