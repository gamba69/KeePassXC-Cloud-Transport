# KeePassXC Cloud Transport full Windows build: audit -> restore -> build -> tests -> NativeAOT -> NuGet pack.
param([switch]$NoPause, [switch]$ValidateOnly, [switch]$SkipAot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Run([string]$Name, [string]$Executable, [string[]]$Arguments) {
        Write-Host "[STEP] $Name" -ForegroundColor Cyan
        & $Executable @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Name returned exit code $LASTEXITCODE" }
        Write-Host "[PASS] $Name" -ForegroundColor Green
    }
    $required = @('README.md','LICENSE','CHANGELOG.md','Directory.Build.props',
        'src/KeePassXC.CloudTransport/KeePassXC.CloudTransport.csproj',
        'tests/KeePassXC.CloudTransport.Tests/KeePassXC.CloudTransport.Tests.csproj',
        'examples/KeePassXC.CloudTransport.Example/KeePassXC.CloudTransport.Example.csproj')
    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing $path" }
        Write-Host "[PASS] SourceAudit / $path"
    }
    $props = [xml](Get-Content 'Directory.Build.props' -Raw)
    $version = $props.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version: $version" }
    # PowerShell 5.1 can unwrap the single Get-ChildItem result into a FileInfo.
    # Force an array before accessing Count, and reject nested changelogs.
    $changelogs = @(Get-ChildItem -LiteralPath $root -Filter 'CHANGELOG.md' -Recurse -File)
    $rootChangelog = Join-Path $root 'CHANGELOG.md'
    if ($changelogs.Count -ne 1 -or
        $changelogs[0].FullName -ne $rootChangelog) {
        throw 'Only one root CHANGELOG.md is permitted'
    }
    Write-Host '[PASS] SourceAudit / single root CHANGELOG.md'
    $changelogText = Get-Content -LiteralPath $rootChangelog -Raw -Encoding UTF8
    if ($changelogText -notmatch ("(?m)^## \[" + [regex]::Escape($version) + "\](?:\s|$)")) {
        throw "Missing CHANGELOG.md entry for $version"
    }
    Write-Host '[PASS] SourceAudit / changelog entry for current version'
    [xml]$project = Get-Content 'src/KeePassXC.CloudTransport/KeePassXC.CloudTransport.csproj' -Raw -Encoding UTF8
    if ($project.Project.PropertyGroup.PackageId -ne 'KeePassXC.CloudTransport' -or
        $project.Project.PropertyGroup.AssemblyName -ne 'KeePassXC.CloudTransport') {
        throw 'Package ID and assembly must be KeePassXC.CloudTransport'
    }
    Write-Host '[PASS] SourceAudit / fixed NuGet package identity'
    $workflows = @('.github/workflows/ci.yml','.github/workflows/release-assets.yml',
                   '.github/workflows/publish-nuget.yml')
    foreach ($workflow in $workflows) {
        if (-not (Test-Path -LiteralPath $workflow -PathType Leaf)) {
            throw "Missing GitHub workflow $workflow"
        }
        Write-Host "[PASS] SourceAudit / $workflow"
    }
    $ci = Get-Content -LiteralPath '.github/workflows/ci.yml' -Raw -Encoding UTF8
    $nuget = Get-Content -LiteralPath '.github/workflows/publish-nuget.yml' -Raw -Encoding UTF8
    $assets = Get-Content -LiteralPath '.github/workflows/release-assets.yml' -Raw -Encoding UTF8
    if ($ci -match 'NuGet/login|id-token: write|contents: write|dotnet nuget push') {
        throw 'Ordinary CI must remain read-only and never publish'
    }
    if ($nuget -notmatch 'NuGet/login@v1' -or
        $nuget -notmatch 'id-token: write' -or
        $nuget -notmatch 'workflow_dispatch:' -or
        $nuget -notmatch 'release-check.ps1' -or
        $assets -notmatch 'workflow_dispatch:' -or
        $assets -match 'dotnet nuget push|NuGet/login') {
        throw 'Release workflows lack required manual controls or isolation'
    }
    Write-Host '[PASS] SourceAudit / release workflows and CI permission boundaries'
    # Guard the draft lookup and pinned workflow tooling against regression.
    $guard = Get-Content -LiteralPath 'scripts/release-check.ps1' -Raw -Encoding UTF8
    if (-not $guard.Contains('releases?per_page=100') -or
        -not $guard.Contains('SourceRoot') -or
        -not $assets.Contains('path: .release-tools') -or
        -not $assets.Contains('contents: write') -or
        -not $assets.Contains('SourceRoot $env:GITHUB_WORKSPACE')) {
        throw 'Draft validation must enumerate releases with write access and pin tooling separately from the tag'
    }
    Write-Host '[PASS] SourceAudit / draft release lookup and pinned validator'
    foreach ($source in Get-ChildItem src -Filter '*.cs' -Recurse -File) {
        $body = Get-Content -LiteralPath $source.FullName -Raw
        if ($body -match '\bKeyPassXCCloudSync\b|\bAvalonia\b|\bWindowsCredentialStore\b|\bPlatformServices\.Current\b') {
            throw "Host application coupling in $($source.FullName)"
        }
    }
    Write-Host '[PASS] SourceAudit / no host application API coupling'
    if ($ValidateOnly) { Write-Host '[SUMMARY] Source validation passed'; exit 0 }
    Run 'Restore library' 'dotnet' @('restore','src/KeePassXC.CloudTransport/KeePassXC.CloudTransport.csproj','--nologo')
    Run 'Restore tests' 'dotnet' @('restore','tests/KeePassXC.CloudTransport.Tests/KeePassXC.CloudTransport.Tests.csproj','--nologo')
    Run 'Build library' 'dotnet' @('build','src/KeePassXC.CloudTransport/KeePassXC.CloudTransport.csproj','-c','Release','--no-restore','--nologo')
    Run 'Build tests' 'dotnet' @('build','tests/KeePassXC.CloudTransport.Tests/KeePassXC.CloudTransport.Tests.csproj','-c','Release','--no-restore','--nologo')
    Run 'Run named regression tests' 'dotnet' @('run','--project','tests/KeePassXC.CloudTransport.Tests/KeePassXC.CloudTransport.Tests.csproj','-c','Release','--no-build','--no-restore')
    if (-not $SkipAot) {
        Run 'NativeAOT Windows smoke' 'dotnet' @('publish','examples/KeePassXC.CloudTransport.Example/KeePassXC.CloudTransport.Example.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:PublishAot=true','-o','publish/aot-smoke','--nologo')
        $exe = Join-Path $root 'publish/aot-smoke/KeePassXC.CloudTransport.Example.exe'
        if (-not (Test-Path $exe)) { throw 'NativeAOT executable missing' }
        Run 'Execute NativeAOT smoke' $exe @()
    } else { Write-Host '[SKIP] NativeAOT smoke / local developer override' }
    # Never package or upload a stale nupkg from a previous build/version.
    if (Test-Path -LiteralPath 'dist') {
        Remove-Item -LiteralPath 'dist' -Force -Recurse
    }
    New-Item -ItemType Directory -Force 'dist' | Out-Null
    Run 'Build NuGet package' 'dotnet' @('pack','src/KeePassXC.CloudTransport/KeePassXC.CloudTransport.csproj','-c','Release','--no-build','--no-restore','-o','dist','--nologo')
    $artifact = Join-Path $root ("dist/KeePassXC.CloudTransport.$version.nupkg")
    if (-not (Test-Path $artifact)) { throw "Missing package $artifact" }
    $sha = (Get-FileHash $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$artifact.sha256", "$sha  $([IO.Path]::GetFileName($artifact))`n", [Text.UTF8Encoding]::new($false))
    Write-Host "[PASS] NuGet / $artifact"
    Write-Host "[PASS] SHA256 / $sha"
    & (Join-Path $PSScriptRoot 'verify-package.ps1') -Version $version -Directory 'dist'
    if ($LASTEXITCODE -ne 0) { throw 'Package verification failed' }
    Write-Host '[SUMMARY] Build completed successfully'
}
catch {
    Write-Host "[FAIL] Build / $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally { Pop-Location }
