<#
  Deltanium Store node installer for Windows.

  Run in an elevated PowerShell (Run as administrator) from the extracted folder:
    .\install.ps1                                   asks a few questions
    .\install.ps1 -Network testnet -Voucher XXXX    no questions for what you pass
    .\install.ps1 -Uninstall                        remove the service (keeps your data and node key)

  It installs the node as the Windows service "DeltaniumStore", reachable by the network over DLTP (TCP) on your
  public IP: no domain, certificate or IIS needed. The node key stays in -DataDir (only Administrators and
  SYSTEM can read it). Forward TCP port -DltpPort on your router to this computer.
#>
param(
    [ValidateSet('mainnet', 'testnet')][string]$Network,
    [string]$Voucher,
    [string]$TcpEndpoint = 'auto',    # auto = the public IP Central sees | tcps://<public IP>:<port> | off
    [string]$HttpsEndpoint = '',      # optional: https://node.your-domain if you also serve it behind HTTPS
    [int]$DltpPort = 7333,
    [int]$HttpPort = 5001,
    [string]$DataDir = "$env:ProgramData\Deltanium\Store",
    [string]$InstallDir = "$env:ProgramFiles\Deltanium\Store",
    [switch]$NonInteractive,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'DeltaniumStore'
$Exe = 'deltanium-store.exe'

function Say($m) { Write-Host $m }
function Ask($q, $default) {
    if ($NonInteractive) { return $default }
    $a = Read-Host "$q$(if ($default) { " [$default]" })"
    if ([string]::IsNullOrWhiteSpace($a)) { return $default } else { return $a.Trim() }
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run this script in PowerShell opened with "Run as administrator".' }

if ($Uninstall) {
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service $ServiceName -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
        Say "Service $ServiceName removed."
    }
    Get-NetFirewallRule -DisplayName "Deltanium Store DLTP" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Say "Your data and node key are still in $DataDir. Delete that folder yourself only if you are leaving the network."
    return
}

Say ''
Say "Deltanium Store node setup"
Say '------------------------------'
if (-not $Network) { $Network = Ask 'Network (mainnet / testnet)' 'mainnet' }
$Central = if ($Network -eq 'testnet') { 'https://testnet-api.deltanium.com' } else { 'https://api.deltanium.com' }

$registered = Test-Path (Join-Path $DataDir 'node_info.json')
if (-not $registered -and -not $Voucher) {
    $Voucher = Ask 'One-shot voucher (see https://github.com/dlvstudio/deltanium/blob/main/VOUCHERS.md)' ''
    if (-not $Voucher) { throw 'A voucher is needed the first time a node registers.' }
}
if (-not $NonInteractive -and -not $PSBoundParameters.ContainsKey('TcpEndpoint')) {
    $TcpEndpoint = Ask "DLTP address: 'auto' (your public IP), 'tcps://<public IP>:<port>', or 'off'" 'auto'
}

Say "Reading the network keys from $Central ..."
$keys = Invoke-RestMethod "$Central/api/explorer/keys" -TimeoutSec 30
$centralKey = $keys.central.publicKey
$superAdminKey = $keys.superAdmin.publicKey
Say "  Central API key : $centralKey"
Say "  Super Admin key : $superAdminKey"
Say '  Compare them with https://deltanium.com/nodes.html and NODE_KEYS.md before you continue.'
if (-not $NonInteractive) { if ((Ask 'Keys match? (y/n)' 'y') -notmatch '^[yY]') { throw 'Stopped: keys not confirmed.' } }

$dltp = switch -Regex ($TcpEndpoint) {
    '^off$' { '' }
    '^auto$' { "tcps://auto:$DltpPort" }
    default { $TcpEndpoint }
}

# Environment of the service (the node reads it at start).
$envVars = [ordered]@{
    'DELTANIUM_HOME' = $DataDir
    'Storage__Provider' = 'Json'
}
if ($Network -eq 'testnet') { $envVars['NETWORK_ID'] = 'deltanium-test' }
if ($dltp) { $envVars['Dltp__Port'] = "$DltpPort"; $envVars['Dltp__PublicEndpoint'] = $dltp }
$envVars['NodeRegistration__ApiUrl'] = $Central
$envVars['CentralApi__BaseUrl'] = $Central
$envVars['CENTRAL_PUBLIC_KEY'] = $centralKey
$envVars['Storage__BasePath'] = (Join-Path $DataDir 'FileStorage')
if ($Voucher) { $envVars['STORE_VOUCHER'] = $Voucher }
if ($HttpsEndpoint) { $envVars['NodeRegistration__Endpoint'] = $HttpsEndpoint }
# The Store sends post transactions to a Block Creator: take one the network lists.
$blocker = @(Invoke-RestMethod "$Central/api/blockcreatornode/list" -TimeoutSec 30) | Where-Object { $_.endpoint } | Select-Object -First 1
if ($blocker) { $envVars['Blockchain__BlockerApi'] = $blocker.endpoint; $envVars['Blockchain__BlockerPublicKey'] = $blocker.publicKey }

New-Item -ItemType Directory -Force $InstallDir, $DataDir | Out-Null
# The node key will live here: Administrators and SYSTEM only.
& icacls $DataDir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) { Stop-Service $ServiceName -ErrorAction SilentlyContinue; Start-Sleep 2 }
Say "Copying the program to $InstallDir ..."
Copy-Item -Path (Join-Path $PSScriptRoot '*') -Destination $InstallDir -Recurse -Force -Exclude 'install.sh'

$bin = "`"$InstallDir\$Exe`" --urls http://127.0.0.1:$HttpPort"
if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $ServiceName -BinaryPathName $bin -DisplayName "Deltanium Store" -StartupType Automatic | Out-Null
} else {
    & sc.exe config $ServiceName binPath= $bin start= auto | Out-Null
}
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
$multi = @($envVars.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Type MultiString -Value $multi

if ($dltp) {
    Get-NetFirewallRule -DisplayName "Deltanium Store DLTP" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName "Deltanium Store DLTP" -Direction Inbound -Protocol TCP -LocalPort $DltpPort -Action Allow | Out-Null
}

Say 'Starting the service ...'
Start-Service $ServiceName
$ok = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep 2
    try { $v = Invoke-RestMethod "http://127.0.0.1:$HttpPort/api/version" -TimeoutSec 5; $ok = $true; break } catch {}
}
if (-not $ok) { throw "The node did not answer on port $HttpPort. See the logs in $DataDir\logs." }
Say "Node running: $($v.service) $($v.version) on $Network."
$info = Join-Path $DataDir 'node_info.json'
if (Test-Path $info) {
    $pk = (Get-Content $info -Raw | ConvertFrom-Json).publicKey
    Say "Node public key: $pk"
    # New nodes join after the registration fee is paid and the Super Admin approves it.
    try {
        $reg = Invoke-RestMethod "$Central/api/noderegistration/$pk" -TimeoutSec 15
        Say ''
        Say "Registration status: $($reg.status)"
        if ($reg.status -eq 'awaiting_payment') {
            Say ("  Fee            : {0:N0} VND" -f [double]$reg.feeVnd)
            Say "  Transfer note  : $($reg.transferContent)"
            if ($reg.bank) { Say "  Bank           : $($reg.bank.bankName) $($reg.bank.accountNo) ($($reg.bank.accountName))" }
            if ($reg.paypal) { Say "  PayPal         : $($reg.paypal.url) (put the transfer note in the message)" }
        }
        Say "  $($reg.instructions)"
        Say "  Status any time: $Central/api/noderegistration/$pk"
    } catch {}
}
Say ''
Say "Next: forward TCP port $DltpPort on your router to this computer's local IP."
Say 'If your provider uses carrier-grade NAT (several homes share one public IP), ask for a public IP or use a VPS.'
Say "Check that the network sees you: https://deltanium.com/nodes.html$(if ($Network -eq 'testnet') { '?net=testnet' })"
