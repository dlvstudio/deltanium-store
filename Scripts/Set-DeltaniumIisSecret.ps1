<#
.SYNOPSIS
  DPAPI-encrypt a secret and attach it to a specific IIS Application Pool
  (so store-1 / store-2 on the same machine can have different secrets).

.DESCRIPTION
  Requires: run as Administrator on the Windows IIS server.
  Apps decrypt values starting with "dpapi:" at startup.

  Uses WebAdministration (not appcmd processModel.*) because App Pool
  environmentVariables live on the pool itself, not under processModel.

.EXAMPLE
  .\Set-DeltaniumIisSecret.ps1 -AppPool "api.deltanium.com" `
    -Name "ConnectionStrings__Default" `
    -PlainValue "Server=127.0.0.1;..."

.EXAMPLE
  .\Set-DeltaniumIisSecret.ps1 -AppPool "store-1.deltanium.com" `
    -Name "STORE_VOUCHER" -PlainValue "voucher-one-shot"
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $AppPool,

    [Parameter(Mandatory = $true)]
    [string] $Name,

    [Parameter(Mandatory = $true)]
    [string] $PlainValue
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security
Import-Module WebAdministration

$bytes = [System.Text.Encoding]::UTF8.GetBytes($PlainValue)
$protected = [System.Security.Cryptography.ProtectedData]::Protect(
    $bytes,
    $null,
    [System.Security.Cryptography.DataProtectionScope]::LocalMachine
)
$encoded = "dpapi:" + [Convert]::ToBase64String($protected)

$filter = "system.applicationHost/applicationPools/add[@name='$AppPool']/environmentVariables"
$existing = Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name "." -ErrorAction SilentlyContinue
if ($existing) {
    $match = @($existing.Collection | Where-Object { $_.name -eq $Name })
    if ($match.Count -gt 0) {
        Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name "." -AtElement @{name = $Name}
    }
}

Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name "." -Value @{ name = $Name; value = $encoded }
Write-Host "OK: AppPool '$AppPool' ← $Name (dpapi). Recycle pool or iisreset when batch is done."
