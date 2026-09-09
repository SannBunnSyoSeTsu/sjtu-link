$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$profileName = 'SJTU Link - Student IKEv2'
$ownerFile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SJTU-Link\profile-owner.txt'

function Normalize-Prefix([string]$value) {
    $parts = $value.Trim().Split('/')
    if ($parts.Count -gt 2 -or $parts[0].Contains('%')) { throw "无效 IP 网段：$value" }
    $ip = $null
    if (-not [Net.IPAddress]::TryParse($parts[0], [ref]$ip)) { throw "无效 IP 地址：$value" }
    if ($ip.AddressFamily -eq 'InterNetwork' -and $parts[0] -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "IPv4 必须写完整四段：$value" }
    $bytes = $ip.GetAddressBytes()
    $bits = $bytes.Length * 8
    $prefix = $bits
    if ($parts.Count -eq 2) {
        if ($parts[1] -notmatch '^\d+$') { throw "无效掩码：$value" }
        $prefix = [int]$parts[1]
    }
    if ($prefix -lt 1 -or $prefix -gt $bits) { throw '规则不接受 /0；请用模式选择设置默认出口。' }
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $keep = [Math]::Max(0, [Math]::Min(8, $prefix - $i * 8))
        $bytes[$i] = $bytes[$i] -band ((255 -shl (8 - $keep)) -band 255)
    }
    return ([Net.IPAddress]::new($bytes)).ToString() + '/' + $prefix
}

function Get-Plan($request) {
    if ($request.Server -notin @('stu.vpn.sjtu.edu.cn', 'stuv4.vpn.sjtu.edu.cn')) { throw '请选择交大学生 VPN 服务器。' }
    if ($request.Mode -notin @('split', 'full')) { throw '无效分流模式。' }
    $routes = @()
    $details = @()
    foreach ($line in @($request.Rules)) {
        $rule = ([string]$line).Trim()
        if (-not $rule -or $rule.StartsWith('#')) { continue }
        $ip = $null
        if ($rule.Contains('/') -or [Net.IPAddress]::TryParse($rule, [ref]$ip)) {
            $prefix = Normalize-Prefix $rule
            $routes += $prefix
            $details += [pscustomobject]@{ Rule = $rule; Prefix = $prefix }
        } else {
            if ($rule.Length -gt 253 -or $rule -notmatch '^(?=.{1,253}$)([a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$') { throw "请输入完整域名或 IP 网段，不支持网址、通配符：$rule" }
            $addresses = @([Net.Dns]::GetHostAddresses($rule) | Where-Object { $_.AddressFamily -in @('InterNetwork', 'InterNetworkV6') })
            if ($addresses.Count -eq 0) { throw "域名没有可用地址：$rule" }
            foreach ($address in $addresses) {
                if ($address.IsIPv6LinkLocal -or [Net.IPAddress]::IsLoopback($address)) { throw "域名解析到了本机或链路本地地址：$rule" }
                $prefix = Normalize-Prefix $address.ToString()
                $routes += $prefix
                $details += [pscustomobject]@{ Rule = $rule; Prefix = $prefix }
            }
        }
    }
    if ($request.Mode -eq 'full') {
        $routes += '2000::/3'
        $details += [pscustomobject]@{ Rule = '默认 VPN：全球单播 IPv6'; Prefix = '2000::/3' }
    }
    $routes = @($routes | Sort-Object -Unique)
    if ($request.Mode -eq 'split' -and $routes.Count -eq 0) { throw '分流模式至少需要一个目标。可先添加 net.sjtu.edu.cn 测试。' }
    if ($routes.Count -gt 512) { throw '最多支持 512 条解析后的路由。' }
    [pscustomobject]@{ Server = $request.Server; Mode = $request.Mode; Routes = $routes; Details = $details; ResolvedAt = (Get-Date).ToString('s') }
}

function Test-ProfileOwnership($p) {
    if (-not $p -or -not (Test-Path -LiteralPath $ownerFile)) { return $false }
    $savedId = [guid]::Empty
    $actualId = [guid]::Empty
    try {
        $saved = [IO.File]::ReadAllText($ownerFile).Trim()
        return ([guid]::TryParse($saved, [ref]$savedId) -and [guid]::TryParse([string]$p.Guid, [ref]$actualId) -and $savedId -ne [guid]::Empty -and $savedId -eq $actualId)
    } catch { return $false }
}

function Get-OurProfile([switch]$RequireOwnership) {
    # Enumeration failures must not be mistaken for an absent profile.
    $profiles = @(Get-VpnConnection -ErrorAction Stop)
    $p = $profiles | Where-Object Name -eq $profileName | Select-Object -First 1
    if ($p -and ($p.ServerAddress -notin @('stu.vpn.sjtu.edu.cn','stuv4.vpn.sjtu.edu.cn') -or [string]$p.TunnelType -ne 'Ikev2')) { throw '同名连接不是交大学生 IKEv2 配置，不能使用本客户端登录或修改。' }
    if ($p -and $RequireOwnership -and -not (Test-ProfileOwnership $p)) { throw '这条连接可以登录，但缺少匹配的管理标记，暂不能修改或删除。请保留现有连接及本地配置标记。' }
    return $p
}

function Apply-Plan($plan) {
    $p = Get-OurProfile -RequireOwnership
    if ($p -and $p.ConnectionStatus -ne 'Disconnected') { throw '请先断开本客户端 VPN，再应用配置。' }
    $oldRoutes = @()
    if ($p) { $oldRoutes = @($p.Routes | Select-Object DestinationPrefix, RouteMetric) }
    $created = $false
    try {
        if (-not $p) {
            $eap = New-EapConfiguration -Peap -VerifyServerIdentity
            Add-VpnConnection -Name $profileName -ServerAddress $plan.Server -TunnelType Ikev2 -AuthenticationMethod Eap -EapConfigXmlStream $eap.EapConfigXmlStream -EncryptionLevel Required -SplitTunneling -DnsSuffix 'sjtu.edu.cn' -RememberCredential:$false -Force | Out-Null
            $created = $true
            $createdProfile = Get-VpnConnection -Name $profileName
            [IO.Directory]::CreateDirectory((Split-Path $ownerFile)) | Out-Null
            [IO.File]::WriteAllText($ownerFile, [string]$createdProfile.Guid)
        }
        Set-VpnConnection -Name $profileName -ServerAddress $plan.Server -SplitTunneling:($plan.Mode -eq 'split') -Force | Out-Null
        foreach ($route in $oldRoutes) {
            Remove-VpnConnectionRoute -ConnectionName $profileName -DestinationPrefix $route.DestinationPrefix -Confirm:$false | Out-Null
        }
        foreach ($prefix in $plan.Routes) {
            Add-VpnConnectionRoute -ConnectionName $profileName -DestinationPrefix $prefix -RouteMetric 1 | Out-Null
        }
        $check = Get-OurProfile -RequireOwnership
        $actual = @($check.Routes | ForEach-Object { $_.DestinationPrefix } | Sort-Object -Unique)
        if ($check.ServerAddress -ne $plan.Server -or $check.SplitTunneling -ne ($plan.Mode -eq 'split') -or @(Compare-Object $actual @($plan.Routes)).Count -gt 0) { throw '写入后的配置校验不一致。' }
    } catch {
        $originalError = $_.Exception.Message
        try {
            if ($created) {
                Remove-VpnConnection -Name $profileName -Force | Out-Null
                if (Test-Path -LiteralPath $ownerFile) { [IO.File]::Delete($ownerFile) }
            }
            elseif ($p) {
                Set-VpnConnection -Name $profileName -ServerAddress $p.ServerAddress -SplitTunneling:$p.SplitTunneling -Force | Out-Null
                $current = Get-OurProfile -RequireOwnership
                foreach ($route in @($current.Routes)) { Remove-VpnConnectionRoute -ConnectionName $profileName -DestinationPrefix $route.DestinationPrefix -Confirm:$false | Out-Null }
                foreach ($route in $oldRoutes) { Add-VpnConnectionRoute -ConnectionName $profileName -DestinationPrefix $route.DestinationPrefix -RouteMetric $route.RouteMetric | Out-Null }
            }
        } catch { throw "应用失败：$originalError；恢复也失败：$($_.Exception.Message)。请在 Windows VPN 设置中检查本工具连接。" }
        throw "应用失败，已恢复此前配置：$originalError"
    }
    return $plan
}

function Get-Status {
    $p = Get-OurProfile
    $proxy = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $warnings = @()
    if ($p -and -not (Test-ProfileOwnership $p)) { $warnings += '旧连接可登录；管理标记未匹配，暂不能修改或删除配置。' }
    if ($proxy.ProxyEnable -eq 1) { $warnings += "系统代理已开启：$($proxy.ProxyServer)。浏览器可能先走代理，目标分流不能决定代理软件的出口。" }
    if ($proxy.AutoConfigURL) { $warnings += '系统启用了自动代理脚本，实际出口也受脚本影响。' }
    $adapters = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object { $_.OperationalStatus -eq 'Up' -and ($_.Name -match 'aTrust' -or $_.Description -match 'aTrust|Sangfor') })
    if ($adapters.Count -gt 0) { $warnings += 'aTrust 隧道网卡仍在线；请先在原客户端注销，以免两条 VPN 的路由相互影响。' }
    [pscustomobject]@{ Exists = [bool]$p; Status = $(if ($p) { [string]$p.ConnectionStatus } else { 'NotConfigured' }); Server = $(if ($p) { $p.ServerAddress } else { '' }); Split = $(if ($p) { $p.SplitTunneling } else { $true }); Routes = @($p.Routes | Where-Object { $_ } | Select-Object DestinationPrefix, RouteMetric); Warnings = $warnings }
}

function Invoke-Request($request) {
    switch ($request.Action) {
        'preview' { Get-Plan $request }
        'apply' {
            # Resolve at preview time; the caller passes the exact reviewed plan.
            $plan = $request.Plan
            if ($plan.Server -notin @('stu.vpn.sjtu.edu.cn','stuv4.vpn.sjtu.edu.cn') -or $plan.Mode -notin @('split','full')) { throw '配置无效。' }
            if (@($plan.Routes).Count -lt 1 -or @($plan.Routes).Count -gt 512) { throw '路由数量无效。' }
            foreach ($prefix in $plan.Routes) { if ((Normalize-Prefix $prefix) -ne $prefix) { throw '路由必须是标准网络地址。' } }
            Apply-Plan $plan
        }
        'status' { Get-Status }
        'remove' {
            $p = Get-OurProfile -RequireOwnership
            if ($p -and $p.ConnectionStatus -ne 'Disconnected') { throw '请先断开 VPN。' }
            if ($p) {
                Remove-VpnConnection -Name $profileName -Force | Out-Null
                if (Test-Path -LiteralPath $ownerFile) { [IO.File]::Delete($ownerFile) }
            }
            '本工具的 VPN 连接已删除。'
        }
        'diagnose' {
            $target = [string]$request.Target
            if ($target.Length -gt 253 -or $target -notmatch '^[a-zA-Z0-9.:-]+$') { throw '请输入单个域名或 IP 地址。' }
            $results = @()
            foreach ($ip in [Net.Dns]::GetHostAddresses($target)) {
                try {
                    $selected = @(Find-NetRoute -RemoteIPAddress $ip.ToString() -ErrorAction Stop)
                    $results += [pscustomobject]@{ Address = $ip.ToString(); Selection = @($selected | Select-Object IPAddress,DestinationPrefix,NextHop,InterfaceAlias,InterfaceIndex,RouteMetric) }
                } catch { $results += [pscustomobject]@{ Address = $ip.ToString(); Error = $_.Exception.Message } }
            }
            [pscustomobject]@{ Target = $target; Results = $results; Notice = '这是系统 IP 路由选择，不代表实际网页出口；代理、其他 VPN、DNS 和服务端策略仍可能影响连接。' }
        }
        default { throw '未知操作。' }
    }
}

if ($env:SJTU_LINK_TEST -ne '1') {
    try {
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $result = Invoke-Request $request
        @{ Ok = $true; Data = $result } | ConvertTo-Json -Depth 14 -Compress
    } catch {
        @{ Ok = $false; Error = $_.Exception.Message } | ConvertTo-Json -Depth 4 -Compress
        exit 1
    }
}
