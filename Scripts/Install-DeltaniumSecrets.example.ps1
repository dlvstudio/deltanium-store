<#
.SYNOPSIS
  Example batch: set DPAPI-protected secrets on each Deltanium IIS App Pool.
  Copy to Install-DeltaniumSecrets.local.ps1 (do not commit real values) and run as Admin on the server.

.NOTES
  App pool names match IIS sites under C:\webapps\<site>.
  After running: iisreset
#>
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

function Set-Secret($pool, $name, $value) {
    & (Join-Path $here "Set-DeltaniumIisSecret.ps1") -AppPool $pool -Name $name -PlainValue $value
}

# Shared internal token (same value on API + Store + Blocker)
$internalToken = "REPLACE_LONG_RANDOM_TOKEN"

# --- API (api.deltanium.com) ---
Set-Secret "api.deltanium.com" "ConnectionStrings__Default" "Server=127.0.0.1;Port=3306;Uid=REPLACE;Pwd=REPLACE;Database=deltanium_api;SslMode=Preferred;CharSet=utf8mb4;"
Set-Secret "api.deltanium.com" "InternalApi__Token" $internalToken
Set-Secret "api.deltanium.com" "INTERNAL_API_TOKEN" $internalToken
# Optional: Set-Secret "api.deltanium.com" "CENTRAL_PRIVATE_KEY" "...."
# Optional: Set-Secret "api.deltanium.com" "CENTRAL_PUBLIC_KEY" "...."

# --- Store 1 ---
Set-Secret "store-1.deltanium.com" "ConnectionStrings__Default" "Server=127.0.0.1;Port=3306;Uid=REPLACE;Pwd=REPLACE;Database=deltanium_store_1;SslMode=Preferred;CharSet=utf8mb4;"
Set-Secret "store-1.deltanium.com" "InternalApi__Token" $internalToken
Set-Secret "store-1.deltanium.com" "CentralApi__BaseUrl" "https://api.deltanium.com"
Set-Secret "store-1.deltanium.com" "NodeRegistration__ApiUrl" "https://api.deltanium.com"
Set-Secret "store-1.deltanium.com" "NodeRegistration__Endpoint" "https://store-1.deltanium.com"
Set-Secret "store-1.deltanium.com" "STORE_VOUCHER" "REPLACE_STORE1_VOUCHER"
Set-Secret "store-1.deltanium.com" "NodeRegistration__Voucher" "REPLACE_STORE1_VOUCHER"

# --- Store 2 ---
Set-Secret "store-2.deltanium.com" "ConnectionStrings__Default" "Server=127.0.0.1;Port=3306;Uid=REPLACE;Pwd=REPLACE;Database=deltanium_store_2;SslMode=Preferred;CharSet=utf8mb4;"
Set-Secret "store-2.deltanium.com" "InternalApi__Token" $internalToken
Set-Secret "store-2.deltanium.com" "CentralApi__BaseUrl" "https://api.deltanium.com"
Set-Secret "store-2.deltanium.com" "NodeRegistration__ApiUrl" "https://api.deltanium.com"
Set-Secret "store-2.deltanium.com" "NodeRegistration__Endpoint" "https://store-2.deltanium.com"
Set-Secret "store-2.deltanium.com" "STORE_VOUCHER" "REPLACE_STORE2_VOUCHER"
Set-Secret "store-2.deltanium.com" "NodeRegistration__Voucher" "REPLACE_STORE2_VOUCHER"

# --- Blocker 1 ---
Set-Secret "blocker-1.deltanium.com" "ConnectionStrings__Default" "Server=127.0.0.1;Port=3306;Uid=REPLACE;Pwd=REPLACE;Database=deltanium_blocker_1;SslMode=Preferred;CharSet=utf8mb4;"
Set-Secret "blocker-1.deltanium.com" "InternalApi__Token" $internalToken
Set-Secret "blocker-1.deltanium.com" "CentralApiUrl" "https://api.deltanium.com"
Set-Secret "blocker-1.deltanium.com" "NODE_ENDPOINT" "https://blocker-1.deltanium.com"
Set-Secret "blocker-1.deltanium.com" "VOUCHER" "REPLACE_BLOCKER_VOUCHER"
Set-Secret "blocker-1.deltanium.com" "NodeRegistration__Voucher" "REPLACE_BLOCKER_VOUCHER"

Write-Host ""
Write-Host "All secrets set. Run: iisreset"
Write-Host "Then check https://api.deltanium.com (should not be 500.30)"
