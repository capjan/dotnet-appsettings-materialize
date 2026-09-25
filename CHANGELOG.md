# Changelog

## [1.0.1] - 2026-09-25

- Fixed empty objects and arrays overriding scalar values at the same key.
- Fixed empty root configuration keys being dropped from the output.
- Output files now keep the permissions and ACLs of an overwritten file, and new files are created with owner-only access.
- Temporary output files are private while they are written on Unix, including cleanup of inherited macOS ACLs.
- Windows atomic replacement now honors the no-overwrite behavior.
- Release binaries are built with the tagged version.
- Aligned the `setup-dotnet` version with `global.json` in CI.
- Simplified the README language and updated the documentation for output permissions.

## [1.0.0] - 2026-09-24

- Initial global .NET tool release.
- Added Microsoft.Extensions.Configuration JSON-layer materialization.
- Added deterministic nested output, roundtrip validation, atomic writes, and CLI checks.
- Added Linux, Windows, and macOS CI coverage.
