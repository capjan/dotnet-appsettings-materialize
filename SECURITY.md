# Security policy

## Scope

This tool reads JSON configuration files and writes their effective values to a new JSON file. It does not manage secrets or sanitize configuration.

Do not include production secrets in issue reports, logs, pull requests, or test fixtures. Use synthetic values when reproducing a problem.

## Reporting a vulnerability

Please report suspected vulnerabilities privately to the repository maintainers before opening a public issue. Include the affected version, operating system, minimal reproduction steps, and impact description without including secret values.

The tool is intentionally conservative: it does not print configuration values, does not delete inputs, refuses accidental output overwrites, and writes output atomically.
