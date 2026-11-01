# =====================================================================
#  死神VS火影 联机助手 —— 一键清理全部残留
#  会删除：程序、防火墙规则、计划任务、快捷方式、虚拟网卡、驱动包
#  不会影响：Radmin VPN、网卡上的其它程序
# =====================================================================

$ErrorActionPreference = 'SilentlyContinue'

function Line($t) { Write-Host $t }
function Title($t) { Write-Host ""; Write-Host "===== $t =====" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "  [完成] $t" -ForegroundColor Green }
function Skip($t) { Write-Host "  [跳过] $t" -ForegroundColor DarkGray }
function Bad($t)  { Write-Host "  [失败] $t" -ForegroundColor Yellow }

# ---------- 权限检查 ----------
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "请用管理员身份运行本脚本！" -ForegroundColor Red
    Write-Host "（右键 → 以管理员身份运行）"
    if ($args -notcontains '-y') { Read-Host "按回车退出" }
    exit 1
}

Write-Host ""
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "   死神VS火影 联机助手 —— 清理工具" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host " 将删除以下内容："
Write-Host "   1. 正在运行的程序进程"
Write-Host "   2. 防火墙规则（联机助手 / vsrelay / taprelay）"
Write-Host "   3. 计划任务"
Write-Host "   4. 程序目录 C:\ProgramData\VSRelay"
Write-Host "   5. 桌面和开始菜单快捷方式"
Write-Host "   6. TAP 虚拟网卡设备"
Write-Host "   7. TAP 驱动包"
Write-Host ""

if ($args -notcontains '-y') {
    $ans = Read-Host "确认清理？输入 Y 继续，其它任意键取消"
    if ($ans -ne 'Y' -and $ans -ne 'y') { Write-Host "已取消"; Start-Sleep 2; exit 0 }
}

# ---------- 1. 结束进程 ----------
Title "1. 结束进程"
$found = $false
for ($i = 0; $i -lt 4; $i++) {
    $ps = Get-Process -Name 'VsRelay','TapRelay' -ErrorAction SilentlyContinue
    if (-not $ps) { break }
    foreach ($p in $ps) {
        $found = $true
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; Ok "结束 $($p.ProcessName) PID $($p.Id)" }
        catch { Bad "无法结束 $($p.ProcessName) PID $($p.Id)" }
    }
    Start-Sleep -Milliseconds 800
}
if (-not $found) { Skip "没有正在运行的进程" }

# ---------- 2. 防火墙规则 ----------
Title "2. 防火墙规则"
$rules = Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {
    $_.DisplayName -match 'vsrelay|联机助手|taprelay' 
}
if ($rules) {
    foreach ($r in $rules) {
        try { Remove-NetFirewallRule -Name $r.Name -ErrorAction Stop; Ok "删除规则 $($r.DisplayName) [$($r.Direction)/$($r.Action)]" }
        catch { Bad "删除规则失败 $($r.DisplayName)" }
    }
} else { Skip "没有相关防火墙规则" }

# ---------- 3. 计划任务 ----------
Title "3. 计划任务"
$tasks = schtasks /query /fo LIST 2>$null | Select-String 'TaskName' | ForEach-Object { ($_ -split ':')[1].Trim() } |
         Where-Object { $_ -match '\\(Vs|Tap|Relay)' }
if ($tasks) {
    foreach ($t in $tasks) {
        schtasks /delete /tn $t /f 2>&1 | Out-Null
        Ok "删除任务 $t"
    }
} else { Skip "没有相关计划任务" }

# ---------- 4. 程序目录 ----------
Title "4. 程序目录"
foreach ($d in @('C:\ProgramData\VSRelay', 'C:\ProgramData\TapRelay')) {
    if (Test-Path $d) {
        Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $d) { Bad "未能完全删除 $d（可能仍在使用）" } else { Ok "已删除 $d" }
    } else { Skip "$d 不存在" }
}

# ---------- 5. 快捷方式 ----------
Title "5. 快捷方式"
$lnks = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) '死神VS火影 联机助手.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) '死神VS火影 联机助手.lnk'),
    (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) '死神VS火影 联机助手.lnk')
)
$any = $false
foreach ($l in $lnks) {
    if ($l -and (Test-Path $l)) { Remove-Item $l -Force -ErrorAction SilentlyContinue; Ok "已删除 $l"; $any = $true }
}
if (-not $any) { Skip "没有快捷方式" }

# ---------- 6. TAP 虚拟网卡设备 ----------
Title "6. TAP 虚拟网卡"
$taps = Get-PnpDevice -Class Net -ErrorAction SilentlyContinue | Where-Object {
    $_.FriendlyName -match 'TAP-Windows|TAP-Windows Adapter'
}
if ($taps) {
    foreach ($t in $taps) {
        Write-Host "  发现: $($t.FriendlyName)  ($($t.InstanceId))"
        $r = pnputil /remove-device "$($t.InstanceId)" 2>&1
        if ($r -match 'success|成功') { Ok "已移除设备 $($t.InstanceId)" }
        else {
            # 兜底：用 devcon 风格再次尝试
            $r2 = pnputil /remove-device "$($t.InstanceId)" /force 2>&1
            if ($r2 -match 'success|成功') { Ok "已强制移除 $($t.InstanceId)" }
            else { Bad "移除失败 $($t.InstanceId)"; Write-Host "    $r" -ForegroundColor DarkGray }
        }
    }
} else { Skip "没有 TAP 虚拟网卡" }

# ---------- 7. TAP 驱动包 ----------
Title "7. TAP 驱动包"
$enum = pnputil /enum-drivers 2>&1
$curInf = $null
$toDelete = @()
foreach ($line in $enum) {
    if ($line -match 'Published Name\s*:\s*(\S+)') { $curInf = $matches[1] }
    if ($line -match 'Original Name\s*:\s*(\S+)' -and $matches[1] -match 'tap|oemvista') {
        if ($curInf) { $toDelete += $curInf }
    }
}
if ($toDelete.Count -gt 0) {
    foreach ($inf in ($toDelete | Select-Object -Unique)) {
        $r = pnputil /delete-driver $inf /uninstall /force 2>&1
        if ($r -match 'success|成功') { Ok "已删除驱动包 $inf" }
        else { Bad "驱动包 $inf 删除失败（重启后可再试）" }
    }
} else { Skip "没有找到 TAP 驱动包" }

# ---------- 8. 残留 IP 地址 ----------
Title "8. 残留虚拟地址"
$aliases = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
    $_.IPAddress -like '10.99.*'
}
if ($aliases) {
    foreach ($a in $aliases) {
        Remove-NetIPAddress -IPAddress $a.IPAddress -InterfaceIndex $a.InterfaceIndex -Confirm:$false -ErrorAction SilentlyContinue
        Ok "移除地址 $($a.IPAddress)（$($a.InterfaceAlias)）"
    }
} else { Skip "没有残留的 10.99.x 地址" }

# ---------- 结果 ----------
Title "清理结果"
Write-Host ""
Write-Host "  剩余进程:   " -NoNewline; Write-Host ((Get-Process -Name 'VsRelay','TapRelay' -ErrorAction SilentlyContinue | Measure-Object).Count)
Write-Host "  剩余目录:   " -NoNewline; Write-Host $(if (Test-Path 'C:\ProgramData\VSRelay') { '仍存在' } else { '已清除' })
Write-Host "  剩余TAP网卡: " -NoNewline
$left = Get-PnpDevice -Class Net -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'TAP-Windows' }
Write-Host $(if ($left) { $left.Count.ToString() + ' 个' } else { '无' })
Write-Host ""
Write-Host "  网卡列表：" -ForegroundColor Cyan
Get-NetAdapter | Select-Object Name, InterfaceDescription, ifIndex, Status | Format-Table -AutoSize
Write-Host ""
Write-Host "清理完成。建议重启一次电脑，让驱动卸载彻底生效。" -ForegroundColor Green
Write-Host ""
if ($args -notcontains '-y') { Read-Host "按回车退出" }
