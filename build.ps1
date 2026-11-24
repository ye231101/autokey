<#
.SYNOPSIS
    Builds all AutoKey packages into .\artifacts.

.DESCRIPTION
    Runs the unit tests, then build\package.cs, which writes the packages:

      AutoKey-<version>-win-x64.exe
      AutoKey-<version>-osx-arm64.dmg and .tar.gz
      AutoKey-<version>-osx-x64.dmg and .tar.gz
      AutoKey-<version>-linux-x64.tar.gz
      AutoKey-<version>-linux-arm64.tar.gz

    The build stops at the first failure. The script can be started from any directory.

.PARAMETER Version
    Version for the packages, for example 1.0.0. Defaults to the version in build\package.cs.

.PARAMETER Runtime
    Build only these runtimes, for example win-x64,linux-x64. Defaults to all of them.

.PARAMETER SkipTests
    Build the packages without running the tests first.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Version 1.0.0

.EXAMPLE
    .\build.ps1 -Runtime win-x64,osx-arm64 -SkipTests
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [string[]] $Runtime,

    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

function Write-Step([string] $Text) {
    Write-Host "`n>>> $Text" -ForegroundColor Cyan
}

function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet $Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required: https://dotnet.microsoft.com/download'
}

$sdk = & dotnet --version
if ($LASTEXITCODE -ne 0 -or [int]($sdk -split '\.')[0] -lt 10) {
    throw "The .NET 10 SDK or later is required (found: $sdk): https://dotnet.microsoft.com/download"
}

$timer = [Diagnostics.Stopwatch]::StartNew()
Push-Location $PSScriptRoot
try {
    if (-not $SkipTests) {
        Write-Step 'Building and running the tests'
        Invoke-Dotnet @('build', 'AutoKey.slnx', '--configuration', 'Release', '--disable-build-servers')
        Invoke-Dotnet @('test', 'AutoKey.slnx', '--configuration', 'Release', '--no-build')
    }

    Write-Step 'Building the packages'
    $packageArgs = @('run', 'build/package.cs', '--')
    if ($Version) {
        $packageArgs += '--version', $Version
    }

    # Split on commas too, because "powershell -File" passes -Runtime a,b as a single string.
    $packageArgs += @($Runtime -split ',' | Where-Object { $_ })
    Invoke-Dotnet $packageArgs

    # Intermediate publish output; the finished packages are directly in .\artifacts.
    Remove-Item 'artifacts/work' -Recurse -Force -ErrorAction SilentlyContinue
}
finally {
    Pop-Location
}

Write-Host ("`nDone in {0:mm\:ss}. Packages are in {1}" -f $timer.Elapsed, (Join-Path $PSScriptRoot 'artifacts')) -ForegroundColor Green
