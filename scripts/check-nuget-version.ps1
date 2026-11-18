# Fail closed if NuGet.org is unreachable or a version already exists.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    $uri = 'https://api.nuget.org/v3-flatcontainer/keepassxc.cloudtransport/index.json'
    $response = Invoke-WebRequest -Uri $uri -Method Get -TimeoutSec 45 -SkipHttpErrorCheck
    if ($response.StatusCode -eq 404) {
        Write-Host '[PASS] NuGet / Package ID is not published yet; publishing is permitted if policy allows creation'
    }
    elseif ($response.StatusCode -eq 200) {
        $versions = @((ConvertFrom-Json $response.Content).versions)
        if ($versions -contains $Version) { throw "Version $Version already exists on NuGet.org; refusing republish" }
        Write-Host '[PASS] NuGet / Requested version does not already exist'
    }
    else { throw "NuGet.org version check returned HTTP $($response.StatusCode)" }
}
catch {
    Write-Host "[FAIL] NuGet / $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
