$ErrorActionPreference = 'Stop'
$env:SJTU_LINK_TEST = '1'
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'backend.ps1'), [Text.Encoding]::UTF8)))
$ownerFile = Join-Path $PSScriptRoot 'test-owner.txt'
$script:passed = 0
function Assert($condition, [string]$label) { if (-not $condition) { throw "FAIL: $label" }; $script:passed++; Write-Output "PASS: $label" }
function Reject([scriptblock]$action, [string]$label) { $failed=$false; try { & $action | Out-Null } catch { $failed=$true }; Assert $failed $label }
Assert ((Normalize-Prefix '192.168.4.199/24') -eq '192.168.4.0/24') 'IPv4 host bits normalized'
Assert ((Normalize-Prefix '2001:db8:abcd::1/48') -eq '2001:db8:abcd::/48') 'IPv6 host bits normalized'
Assert ((Normalize-Prefix '202.120.2.100') -eq '202.120.2.100/32') 'IPv4 host route'
Reject { Normalize-Prefix '10.1/16' } 'Reject ambiguous IPv4 shorthand'
Reject { Normalize-Prefix '1.2.3.4/33' } 'Reject invalid prefix length'
Reject { Normalize-Prefix '0.0.0.0/0' } 'Reject default route in split rules'
Reject { Normalize-Prefix 'fe80::1%12/64' } 'Reject scoped IPv6 route'
Reject { Get-Plan @{ Server='example.org'; Mode='split'; Rules=@('1.2.3.4') } } 'Reject unapproved server'
Reject { Get-Plan @{ Server='stu.vpn.sjtu.edu.cn'; Mode='split'; Rules=@() } } 'Reject empty split plan'
Reject { Get-Plan @{ Server='stu.vpn.sjtu.edu.cn'; Mode='split'; Rules=@('*.sjtu.edu.cn') } } 'Reject wildcard domain'
Reject { Get-Plan @{ Server='stu.vpn.sjtu.edu.cn'; Mode='split'; Rules=@('https://net.sjtu.edu.cn') } } 'Reject URLs in route rules'
$plan = Get-Plan @{ Server='stu.vpn.sjtu.edu.cn'; Mode='split'; Rules=@('192.168.4.199/24','192.168.4.0/24','2001:db8::1') }
Assert ($plan.Routes.Count -eq 2) 'Deduplicate routes'
$full = Get-Plan @{ Server='stu.vpn.sjtu.edu.cn'; Mode='full'; Rules=@() }
Assert ($full.Routes -contains '2000::/3') 'Default VPN includes global IPv6'

# In-memory Windows VPN command fakes. No real network settings are changed.
$script:state = $null
$script:failAdd = $false
$script:denyRead = $false
$script:writes = 0
function Get-VpnConnection {
    param($Name)
    if ($script:denyRead) { throw 'Simulated access denial' }
    if($script:state) { return ($script:state | ConvertTo-Json -Depth 8 | ConvertFrom-Json) }
}
function New-EapConfiguration { param([switch]$Peap,[switch]$VerifyServerIdentity); @{ EapConfigXmlStream=[xml]'<test />' } }
function Add-VpnConnection {
    param($Name,$ServerAddress,$TunnelType,$AuthenticationMethod,$EapConfigXmlStream,$EncryptionLevel,[switch]$SplitTunneling,$DnsSuffix,$RememberCredential,[switch]$Force)
    $script:writes++
    $script:state = [pscustomobject]@{ Name=$Name; ServerAddress=$ServerAddress; Guid='{11111111-2222-3333-4444-555555555555}'; TunnelType='Ikev2'; AuthenticationMethod=@('Eap'); DnsSuffix=$DnsSuffix; ConnectionStatus='Disconnected'; SplitTunneling=$true; Routes=@() }
}
function Set-VpnConnection { param($Name,$ServerAddress,$SplitTunneling,[switch]$Force); $script:writes++; $script:state.ServerAddress=$ServerAddress; $script:state.SplitTunneling=$SplitTunneling }
function Add-VpnConnectionRoute {
    param($ConnectionName,$DestinationPrefix,$RouteMetric)
    $script:writes++
    if($script:failAdd) { $script:failAdd=$false; throw 'Simulated route write failure' }
    $script:state.Routes += [pscustomobject]@{ DestinationPrefix=$DestinationPrefix; RouteMetric=$RouteMetric }
}
function Remove-VpnConnectionRoute { param($ConnectionName,$DestinationPrefix,$Confirm); $script:writes++; $script:state.Routes=@($script:state.Routes | Where-Object DestinationPrefix -ne $DestinationPrefix) }
function Remove-VpnConnection { param($Name,[switch]$Force); $script:writes++; $script:state=$null }

try {
    Apply-Plan $plan | Out-Null
    Assert ($script:state.Routes.Count -eq 2 -and $script:state.SplitTunneling) 'Create isolated split profile and verify'
    Assert (Test-Path -LiteralPath $ownerFile) 'Record profile ownership GUID'
    $before = $script:state | ConvertTo-Json -Depth 8 -Compress
    $script:failAdd=$true
    Reject { Apply-Plan $full } 'Propagate partial route write failure'
    Assert (($script:state | ConvertTo-Json -Depth 8 -Compress) -eq $before) 'Rollback server, mode, routes, metrics'
    $script:state.ConnectionStatus='Connected'; $writesBefore=$script:writes
    Reject { Apply-Plan $full } 'Reject editing a connected profile'
    Assert ($script:writes -eq $writesBefore) 'Connected profile rejected before mutation'
    $script:state.ConnectionStatus='Disconnected'
    [IO.File]::WriteAllText($ownerFile,'another-guid'); $writesBefore=$script:writes
    Reject { Apply-Plan $full } 'Reject profile ownership mismatch'
    Assert ($script:writes -eq $writesBefore) 'Foreign profile left untouched'
    Assert ((Get-Ownership $script:state) -eq 'invalid') 'Diagnose malformed marker'
    [IO.File]::Delete($ownerFile)
    Assert ((Get-Ownership $script:state) -eq 'missing') 'Diagnose missing marker'
    Assert ($null -ne (Get-OurProfile)) 'Existing school profile remains readable without a marker'
    Reject { Invoke-Request @{Action='restore-owner'; ProfileId='changed';Server=$script:state.ServerAddress} } 'Reject recovery when profile changed since preview'
    $snapshot=$script:state | ConvertTo-Json -Depth 8 -Compress
    $writesBefore=$script:writes
    Invoke-Request @{Action='restore-owner';ProfileId=$script:state.Guid;Server=$script:state.ServerAddress} | Out-Null
    Assert ((Get-Ownership $script:state) -eq 'matched') 'Recover missing ownership record'
    Assert ($script:writes -eq $writesBefore -and ($script:state | ConvertTo-Json -Depth 8 -Compress) -eq $snapshot) 'Recovery does not mutate VPN settings'
    Apply-Plan $full | Out-Null
    Assert (-not $script:state.SplitTunneling) 'Recovered profile can apply configuration'
    [IO.File]::WriteAllText($ownerFile,'{99999999-2222-3333-4444-555555555555}')
    Assert ((Get-Ownership $script:state) -eq 'mismatch') 'Diagnose genuine GUID mismatch'
    Invoke-Request @{Action='restore-owner';ProfileId=$script:state.Guid;Server=$script:state.ServerAddress} | Out-Null
    Assert ((Get-Ownership $script:state) -eq 'matched' -and [IO.File]::Exists($ownerFile+'.bak')) 'Recover stale record and preserve previous marker'
    $script:state.ServerAddress='example.invalid'
    Reject { Invoke-Request @{Action='restore-owner';ProfileId=$script:state.Guid;Server=$script:state.ServerAddress} } 'Do not take over foreign endpoint'
    $script:state.ServerAddress='stu.vpn.sjtu.edu.cn'
    [IO.File]::WriteAllText($ownerFile,'11111111-2222-3333-4444-555555555555')
    Assert ((Get-Ownership $script:state) -eq 'matched') 'Match equivalent GUID formatting'
    $script:denyRead=$true; $writesBefore=$script:writes
    Reject { Apply-Plan $full } 'Read failure is not treated as missing connection'
    Assert ($script:writes -eq $writesBefore) 'Read failure causes no network writes'
    $script:denyRead=$false
    Invoke-Request @{ Action='remove' } | Out-Null
    Assert ($null -eq $script:state -and -not (Test-Path -LiteralPath $ownerFile)) 'Remove owned profile and marker'
    $script:failAdd=$true
    Reject { Apply-Plan $plan } 'New profile route failure surfaced'
    Assert ($null -eq $script:state -and -not (Test-Path -LiteralPath $ownerFile)) 'Failed first setup removes incomplete profile'
    Write-Output "$script:passed checks passed; no real VPN settings changed."
} finally {
    if(Test-Path -LiteralPath $ownerFile) { [IO.File]::Delete($ownerFile) }
    Remove-Item Env:\SJTU_LINK_TEST
}
