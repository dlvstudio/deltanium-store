<#
.SYNOPSIS
  Encrypt a secret with Windows DPAPI (LocalMachine) and set it as a Machine env var.
  Format stored: dpapi:{base64} — Deltanium apps decrypt this at startup.

.EXAMPLE
  .\Protect-DpapiEnv.ps1 -Name "INTERNAL_API_TOKEN" -PlainValue "super-long-token"

.EXAMPLE
  # Re-encrypt an existing plaintext Machine env var in place:
  .\Protect-DpapiEnv.ps1 -Name "ConnectionStrings__Default" -ReencryptExisting
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $Name,

    [Parameter(Mandatory = $false)]
    [string] $PlainValue,

    [switch] $ReencryptExisting
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security

if ($ReencryptExisting) {
    $PlainValue = [System.Environment]::GetEnvironmentVariable($Name, "Machine")
    if ([string]::IsNullOrWhiteSpace($PlainValue)) {
        throw "Machine env '$Name' is empty. Pass -PlainValue instead."
    }
    if ($PlainValue.StartsWith("dpapi:", [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "Already DPAPI-protected: $Name"
        exit 0
    }
}

if ([string]::IsNullOrEmpty($PlainValue)) {
    throw "Provide -PlainValue or -ReencryptExisting"
}

$bytes = [System.Text.Encoding]::UTF8.GetBytes($PlainValue)
$protected = [System.Security.Cryptography.ProtectedData]::Protect(
    $bytes,
    $null,
    [System.Security.Cryptography.DataProtectionScope]::LocalMachine
)
$encoded = "dpapi:" + [Convert]::ToBase64String($protected)

[System.Environment]::SetEnvironmentVariable($Name, $encoded, "Machine")
Write-Host "OK: set Machine env $Name (dpapi, LocalMachine). Run iisreset after batch."
