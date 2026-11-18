# Validate a tag, its source commit and the exact GitHub Release state.
# This script never mutates GitHub resources.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory=$true)][ValidateSet('Draft','Published')][string]$Phase,
    [string]$Repository = 'gamba69/KeePassXC-Cloud-Transport',
    [string]$ExpectedReleaseId = '',
    [string]$ExpectedTagCommit = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Release([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "[PASS] ReleaseGuard / $Message"
}
function Git-Read([string[]]$Arguments) {
    $text = & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed" }
    return ($text | Out-String).Trim()
}
function Gh-Read([string[]]$Arguments) {
    $text = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments -join ' ') failed" }
    return ($text | Out-String).Trim()
}

try {
    Assert-Release ($Repository -ceq 'gamba69/KeePassXC-Cloud-Transport') 'Expected publisher repository'
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
        Assert-Release ($env:GITHUB_REPOSITORY -ceq $Repository) 'Workflow belongs to expected repository'
        Assert-Release ($env:GITHUB_REF -ceq 'refs/heads/main') 'Release dispatched from main branch'
    }
    $root = Split-Path -Parent $PSScriptRoot
    Push-Location $root
    try {
        [xml]$props = Get-Content -LiteralPath 'Directory.Build.props' -Raw -Encoding UTF8
        $sourceVersion = [string]$props.Project.PropertyGroup.Version
        Assert-Release ($sourceVersion -ceq $Version) 'Release version equals Directory.Build.props'
        $changeLog = Get-Content -LiteralPath 'CHANGELOG.md' -Raw -Encoding UTF8
        Assert-Release ($changeLog -match "(?m)^## \[$([regex]::Escape($Version))\](?:\s|$)") 'Release has changelog entry'
        $head = Git-Read @('rev-parse','HEAD')
        $tagCommit = Git-Read @('rev-parse',"refs/tags/$Version^{commit}")
        Assert-Release ($head -ceq $tagCommit) 'Checkout HEAD matches immutable tag target'
        if ($ExpectedTagCommit) {
            Assert-Release ($head -ceq $ExpectedTagCommit) 'Tag target unchanged since preflight'
        }
        & git merge-base --is-ancestor $head 'origin/main'
        Assert-Release ($LASTEXITCODE -eq 0) 'Tag commit is contained in main history'

        $release = Gh-Read @('api',"repos/$Repository/releases/tags/$Version") | ConvertFrom-Json
        Assert-Release ([string]$release.tag_name -ceq $Version) 'Release tag matches source version'
        Assert-Release (-not [bool]$release.prerelease) 'Release is not marked as prerelease'
        if ($ExpectedReleaseId) {
            Assert-Release ([string]$release.id -ceq $ExpectedReleaseId) 'Release identity unchanged'
        }
        $assets = @($release.assets | Where-Object { $null -ne $_ })
        if ($Phase -eq 'Draft') {
            Assert-Release ([bool]$release.draft) 'Release is still a draft'
            Assert-Release ($assets.Count -eq 0) 'Draft has no assets: refusing overwrite/retry over partial upload'
        }
        else {
            Assert-Release (-not [bool]$release.draft) 'Release is already published'
            $packageName = "KeePassXC.CloudTransport.$Version.nupkg"
            $expectedNames = @($packageName, "$packageName.sha256")
            Assert-Release ($assets.Count -eq 2) 'Published release contains exactly two assets'
            foreach ($name in $expectedNames) {
                $matching = @($assets | Where-Object { $_.name -ceq $name -and $_.state -eq 'uploaded' -and $_.size -gt 0 })
                Assert-Release ($matching.Count -eq 1) "Published asset present and uploaded: $name"
            }
            # GitHub immutable release attestations are issued by GitHub upon publication.
            # A failed verification blocks NuGet publication, even if SHA256 is valid.
            [void](Gh-Read @('release','verify',$Version,'--repo',$Repository))
            Write-Host '[PASS] ReleaseGuard / GitHub release attestation verified'
        }
        if ($env:GITHUB_OUTPUT) {
            Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding UTF8 -Value "release_id=$($release.id)"
            Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding UTF8 -Value "tag_commit=$head"
        }
        Write-Host "[SUMMARY] ReleaseGuard / $Phase $Version passed"
    }
    finally { Pop-Location }
}
catch {
    Write-Host "[FAIL] ReleaseGuard / $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
