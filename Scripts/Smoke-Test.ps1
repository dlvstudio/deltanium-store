<#
.SYNOPSIS
  Post-deploy smoke tests for Store nodes. Read-only: never creates data, never uses a key or token.
  Runs after every deploy (testnet, then mainnet); any failure fails the pipeline stage.

.EXAMPLE
  ./Scripts/Smoke-Test.ps1 -Site testnet-store-1.deltanium.com,testnet-store-2.deltanium.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Site,
    [string]$ExpectCommit = ''
)
$ErrorActionPreference = 'Stop'
$Site = @($Site | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
Add-Type -AssemblyName System.Net.Http
$script:http = [System.Net.Http.HttpClient]::new()
$script:http.Timeout = [TimeSpan]::FromSeconds(30)
$script:failures = 0

function Invoke-Http([string]$Method, [string]$Url, [hashtable]$Headers = @{}, [string]$Body = $null) {
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Url)
    foreach ($k in $Headers.Keys) { [void]$req.Headers.TryAddWithoutValidation($k, [string]$Headers[$k]) }
    if ($Method -ne 'GET') {
        $req.Content = [System.Net.Http.StringContent]::new($(if ($Body) { $Body } else { '' }), [Text.Encoding]::UTF8, 'application/json')
    }
    $resp = $script:http.SendAsync($req).GetAwaiter().GetResult()
    [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() }
}

function Check([string]$Name, [scriptblock]$Test) {
    try {
        $detail = & $Test
        Write-Host "PASS $Name $detail"
    } catch {
        $script:failures++
        Write-Host "FAIL $Name : $($_.Exception.Message)"
        Write-Host "##vso[task.logissue type=error]Smoke FAIL $Name : $($_.Exception.Message)"
    }
}

function Expect-Status($r, [int[]]$Want) {
    if ($Want -notcontains $r.Status) { throw "HTTP $($r.Status), want $($Want -join '/'): $($r.Body.Substring(0, [Math]::Min(160, $r.Body.Length)))" }
    "($($r.Status))"
}

function Expect-NoPrivateFields([string]$Json, [string[]]$Fields) {
    foreach ($f in $Fields) {
        if ($Json -match ('"' + $f + '"\s*:')) { throw "response exposes '$f'" }
    }
}
foreach ($s in $Site) {
    $b = "https://$s"
    Write-Host "=== Store smoke: $b"

    Check 'version' {
        $v = (Invoke-Http GET "$b/api/version").Body | ConvertFrom-Json
        if (-not $v.commit) { throw 'no commit' }
        if ($ExpectCommit -and $v.commit -ne $ExpectCommit) { throw "serves $($v.commit), want $ExpectCommit" }
        "$($v.commitShort) network=$($v.network)"
    }
    Check 'blocks stored outside the site folder (deploys cannot delete them)' {
        $v = (Invoke-Http GET "$b/api/version").Body | ConvertFrom-Json
        if ($v.storageOutsideSite -ne $true) { throw "storageOutsideSite=$($v.storageOutsideSite)" }
    }
    Check 'proof challenge needs a Central signature' {
        Expect-Status (Invoke-Http POST "$b/api/proof/challenge" @{} '{"blockId":"x","nonce":"y"}') 401 | Out-Null
        Expect-Status (Invoke-Http POST "$b/api/proof/challenge" @{ 'X-Internal-Token' = 'smoke-test' } '{"blockId":"x","nonce":"y"}') 401
    }
    Check 'egress drain needs a Central signature' {
        Expect-Status (Invoke-Http POST "$b/api/proof/egress/drain" @{ 'X-Internal-Token' = 'smoke-test' } '{}') 401
    }
}

$script:http.Dispose()
if ($script:failures -gt 0) { throw "$($script:failures) smoke check(s) failed" }
Write-Host 'All Store smoke checks passed'
