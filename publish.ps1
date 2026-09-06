<#
.SYNOPSIS
  Publish the DocDr desktop app.

.DESCRIPTION
  Wraps `dotnet publish` for src/DocDr.App with the two deployment shapes:

    -Mode self-contained       bundles the .NET runtime — runs with no .NET installed (default)
    -Mode framework-dependent  needs the .NET 9 Desktop Runtime on the target machine (smaller)

  Add -SingleFile to pack the build into one DocDr.App.exe — works with either mode
  (native libraries, including pdfium.dll, are self-extracted to a temp folder on first
  run). Framework-dependent + single file is the smallest single .exe but still needs
  the runtime installed.

.EXAMPLE
  pwsh publish.ps1
  pwsh publish.ps1 -Mode framework-dependent
  pwsh publish.ps1 -SingleFile
  pwsh publish.ps1 -Mode framework-dependent -SingleFile
#>
[CmdletBinding()]
param(
    [ValidateSet('self-contained', 'framework-dependent')]
    [string]$Mode = 'self-contained',

    [switch]$SingleFile,

    [string]$Runtime = 'win-x64',

    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src/DocDr.App/DocDr.App.csproj'

$selfContained = $Mode -eq 'self-contained'

if (-not $OutputDir) {
    $suffix = if ($SingleFile) { "$Mode-singlefile" } else { $Mode }
    $OutputDir = Join-Path $root "publish/$suffix"
}

# ReadyToRun only for self-contained: for a framework-dependent build the target's runtime
# patch version can differ from the one used here, and R2R + single file fail-fasts on that
# instead of falling back to JIT.
$readyToRun = $selfContained

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    "--self-contained=$($selfContained.ToString().ToLowerInvariant())",
    "-p:PublishReadyToRun=$($readyToRun.ToString().ToLowerInvariant())",
    "-p:PublishSingleFile=$($SingleFile.IsPresent.ToString().ToLowerInvariant())",
    '-o', $OutputDir
)
if ($SingleFile) {
    $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'
}

Write-Host "dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed ($LASTEXITCODE)"
}

$exe = Join-Path $OutputDir 'DocDr.App.exe'
Write-Host ''
Write-Host "Published $Mode$(if ($SingleFile) { ' (single file)' })" -ForegroundColor Green
Write-Host "  $exe"
