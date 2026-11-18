# Validate the exact distributable nupkg and its SHA-256 sidecar.
# Cross-platform PowerShell 7, used before GitHub asset upload and NuGet push.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory=$true)][string]$Directory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    $dir = (Resolve-Path -LiteralPath $Directory).Path
    $name = "KeePassXC.CloudTransport.$Version.nupkg"
    $package = Join-Path $dir $name
    $sidecar = "$package.sha256"
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "Missing package $name" }
    if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf)) { throw 'Missing SHA-256 sidecar' }
    $files = @(Get-ChildItem -LiteralPath $dir -File)
    if ($files.Count -ne 2 -or @($files | Where-Object { $_.Name -notin @($name,"$name.sha256") }).Count -ne 0) {
        throw 'Release directory must contain exactly the nupkg and its SHA-256 sidecar'
    }
    $sidecarText = (Get-Content -LiteralPath $sidecar -Raw -Encoding UTF8).Trim()
    if ($sidecarText -cmatch '^([a-f0-9]{64})  (.+)$') {
        $expectedDigest = $Matches[1]
        if ($Matches[2] -cne $name) { throw 'SHA-256 sidecar references wrong package filename' }
    }
    else { throw 'Malformed SHA-256 sidecar' }
    $actual = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $expectedDigest) { throw 'Package digest differs from SHA-256 sidecar' }
    Write-Host '[PASS] PackageAudit / SHA-256 matches'

    Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entries = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
        $nuspecs = @($entries | Where-Object { $_.FullName -ceq 'KeePassXC.CloudTransport.nuspec' })
        if ($nuspecs.Count -ne 1) { throw 'Expected exactly one package .nuspec' }
        foreach ($required in @('README.md','lib/net10.0/KeePassXC.CloudTransport.dll')) {
            if (@($entries | Where-Object { $_.FullName -ceq $required }).Count -ne 1) {
                throw "Package payload missing $required"
            }
        }
        $reader = [System.IO.StreamReader]::new($nuspecs[0].Open())
        try { [xml]$nuspec = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $namespace = [System.Xml.XmlNamespaceManager]::new($nuspec.NameTable)
        $namespace.AddNamespace('n','http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd')
        $id = $nuspec.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="id"]')
        $nversion = $nuspec.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="version"]')
        if (-not $id -or $id.InnerText -cne 'KeePassXC.CloudTransport') { throw 'Package ID mismatch' }
        if (-not $nversion -or $nversion.InnerText -cne $Version) { throw 'Package version mismatch' }
    }
    finally { $archive.Dispose() }
    Write-Host '[PASS] PackageAudit / NuGet metadata and DLL payload match'
    Write-Host "[SUMMARY] PackageAudit / $name verified"
}
catch {
    Write-Host "[FAIL] PackageAudit / $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
