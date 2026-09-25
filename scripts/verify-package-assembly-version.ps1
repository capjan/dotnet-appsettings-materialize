param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [Parameter(Mandatory = $true)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'

function Test-InformationalVersion {
    param(
        [string] $ExpectedVersion,
        [string] $ActualVersion
    )

    if ($ActualVersion -eq $ExpectedVersion) {
        return $true
    }

    $revisionSeparator = if ($ExpectedVersion.Contains('+')) { '.' } else { '+' }
    return $ActualVersion.StartsWith($ExpectedVersion + $revisionSeparator, [System.StringComparison]::Ordinal)
}

$regressionCases = @(
    @{
        ExpectedVersion = '9.9.9'
        ActualVersion = '9.9.9+0123456789abcdef'
        IsValid = $true
    },
    @{
        ExpectedVersion = '9.9.9+meta'
        ActualVersion = '9.9.9+meta.0123456789abcdef'
        IsValid = $true
    },
    @{
        ExpectedVersion = '9.9.9+meta'
        ActualVersion = '9.9.9+meta+0123456789abcdef'
        IsValid = $false
    }
)

foreach ($case in $regressionCases) {
    $isValid = Test-InformationalVersion -ExpectedVersion $case.ExpectedVersion -ActualVersion $case.ActualVersion
    if ($isValid -ne $case.IsValid) {
        throw "Informational-version regression for $($case.ExpectedVersion): $($case.ActualVersion)."
    }
}

$packages = @(Get-ChildItem -Path $PackagePath -File)
if ($packages.Count -ne 1) {
    throw "Expected one NuGet package at '$PackagePath', found $($packages.Count)."
}

$extractDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
New-Item -ItemType Directory -Path $extractDirectory | Out-Null

try {
    Add-Type -AssemblyName System.IO.Compression.ZipFile
    [System.IO.Compression.ZipFile]::ExtractToDirectory($packages[0].FullName, $extractDirectory)

    $assemblyFile = Get-ChildItem (Join-Path $extractDirectory 'tools') -Filter 'AppSettings.Materializer.Cli.dll' -Recurse | Select-Object -First 1
    if ($null -eq $assemblyFile) {
        throw 'Packaged CLI assembly was not found.'
    }

    $fileVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyFile.FullName)
    $fileVersion = $fileVersionInfo.FileVersion
    $informationalVersion = $fileVersionInfo.ProductVersion
    $fileVersionBase = ($Version -split '[-+]')[0]
    $expectedFileVersion = "$fileVersionBase.0"

    if ($fileVersion -ne $expectedFileVersion) {
        throw "Expected AssemblyFileVersion $expectedFileVersion, found $fileVersion."
    }

    if (-not (Test-InformationalVersion -ExpectedVersion $Version -ActualVersion $informationalVersion)) {
        throw "Expected AssemblyInformationalVersion to match '$Version' with an optional source revision, found '$informationalVersion'."
    }

    Write-Host "Verified package assembly versions: $fileVersion; $informationalVersion"
}
finally {
    Remove-Item -Path $extractDirectory -Recurse -Force
}
