$ErrorActionPreference = 'Stop'

$dir     = Join-Path $PSScriptRoot '..\src\bin\Release\net9.0-windows'
$exe     = Join-Path $dir 'ShortcutManager.exe'
$cfg     = Join-Path $dir 'config.json'
$shotDir = Join-Path $PSScriptRoot 'shots'
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

function Stop-App { Get-Process ShortcutManager -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 400 }
function Cn([int[]]$cp) { -join ($cp | ForEach-Object { [char]$_ }) }

Write-Host '######## 1. logic tests (--test) ########'
Stop-App
Remove-Item (Join-Path $dir 'logic-test.log') -Force -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--test' -PassThru -Wait
Get-Content (Join-Path $dir 'logic-test.log') -Encoding UTF8 |
    Where-Object { $_ -match 'RESULT|FAIL' } | ForEach-Object { "  $_" }
Write-Host ("  exit = {0}" -f $p.ExitCode)

Write-Host ''
Write-Host '######## 2. icon + config selftest (--selftest) ########'
Stop-App
Remove-Item (Join-Path $dir 'selftest.log') -Force -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--selftest' -PassThru -Wait
Get-Content (Join-Path $dir 'selftest.log') -Encoding UTF8 | ForEach-Object { "  $_" }
Write-Host ("  exit = {0}" -f $p.ExitCode)

Write-Host ''
Write-Host '######## 3. startup timing (15 cold runs) ########'
Stop-App
$times = @()
for ($i = 0; $i -lt 15; $i++) {
    Stop-App
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $exe -PassThru
    for ($k = 0; $k -lt 600; $k++) {
        $q = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if ($null -eq $q) { break }
        if ($q.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 5
    }
    $sw.Stop()
    $times += [int]$sw.ElapsedMilliseconds
}
Stop-App
$sorted = $times | Sort-Object
Write-Host ("  runs   : {0}" -f ($times -join ', '))
Write-Host ("  min={0} ms  median={1} ms  max={2} ms" -f $sorted[0], $sorted[[int]($sorted.Count / 2)], $sorted[-1])

Write-Host ''
Write-Host '######## 4. tray behavior (--traytest) ########'
# 最小化->托盘->右键不恢复->单击左键恢复->关闭。
# 断言里有关键的 Win32 层检查（IsIconic / 前台窗口）：只看 WPF 属性会假绿
# ——曾经 WPF 报 state=Normal 而窗口真身还是最小化的。
Stop-App
Remove-Item (Join-Path $dir 'tray-test.log') -Force -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--traytest' -PassThru -Wait
Get-Content (Join-Path $dir 'tray-test.log') -Encoding UTF8 | ForEach-Object { "  $_" }
Write-Host ("  exit = {0}" -f $p.ExitCode)

Write-Host ''
Write-Host '######## 5. window UI behavior (--uitest) ########'
# 最大化已禁用 / 标签栏翻页箭头的显隐与边界 / Ctrl+Shift 多选批量删除
Stop-App
Remove-Item (Join-Path $dir 'ui-test.log') -Force -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--uitest' -PassThru -Wait
Get-Content (Join-Path $dir 'ui-test.log') -Encoding UTF8 | ForEach-Object { "  $_" }
Write-Host ("  exit = {0}" -f $p.ExitCode)

Write-Host ''
Write-Host '######## 6. screenshots (rendered by the app itself) ########'
$sandbox = Join-Path $env:TEMP 'sm-final-data'
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
foreach ($n in 'AlphaDocs', 'BetaImages', 'GammaDL', 'DeltaVault') {
    New-Item -ItemType Directory -Force -Path (Join-Path $sandbox $n) | Out-Null
}
foreach ($n in 'readme.txt', 'notes.md', 'report.docx', 'data.xlsx', 'photo.png') {
    Set-Content -Path (Join-Path $sandbox $n) -Value 'x' -Encoding UTF8
}

# --- 6a. 首次运行（无配置），验证居中 ---
Stop-App
Remove-Item $cfg -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dir 'shot.log') -Force -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--shot', (Join-Path $shotDir 'final-01-empty.png') -PassThru -Wait
Get-Content (Join-Path $dir 'shot.log') | ForEach-Object { "  $_" }
Write-Host ("  exit = {0}" -f $p.ExitCode)

# --- 6b. 有数据 ---
Stop-App
$g1 = Cn @(0x5E38, 0x7528)
$g2 = Cn @(0x5DE5, 0x4F5C)
$g3 = Cn @(0x5A31, 0x4E50)
$g4 = Cn @(0x5B66, 0x4E60)
$n1 = Cn @(0x8BB0, 0x4E8B, 0x672C)

$items = @()
foreach ($n in 'AlphaDocs', 'BetaImages', 'GammaDL', 'DeltaVault') {
    $items += @{ Path = (Join-Path $sandbox $n); DisplayName = ''; CachedIcon = $null }
}
foreach ($n in 'readme.txt', 'notes.md', 'report.docx', 'data.xlsx', 'photo.png') {
    $items += @{ Path = (Join-Path $sandbox $n); DisplayName = ''; CachedIcon = $null }
}
$items += @{ Path = 'C:\Windows\System32\notepad.exe'; DisplayName = $n1; CachedIcon = $null }
$items += @{ Path = 'C:\Windows\explorer.exe'; DisplayName = ''; CachedIcon = $null }
$items += @{ Path = 'C:\Windows\System32\calc.exe'; DisplayName = ''; CachedIcon = $null }
$items += @{ Path = 'C:\Windows\System32\mspaint.exe'; DisplayName = ''; CachedIcon = $null }

$groups = @(
    @{ Id = 'g1'; Name = $g1; Items = $items },
    @{ Id = 'g2'; Name = $g2; Items = @() },
    @{ Id = 'g3'; Name = $g3; Items = @() },
    @{ Id = 'g4'; Name = $g4; Items = @() }
)
[System.IO.File]::WriteAllText($cfg, (ConvertTo-Json $groups -Depth 6), [System.Text.UTF8Encoding]::new($false))
Remove-Item (Join-Path $dir 'shot.log') -Force -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--shot', (Join-Path $shotDir 'final-02-full.png') -PassThru -Wait
Get-Content (Join-Path $dir 'shot.log') | ForEach-Object { "  $_" }
Write-Host ("  exit = {0}" -f $p.ExitCode)

# --- 6c. 分组很多：标签栏应出现右侧翻页箭头 ---
Stop-App
$many = @()
foreach ($i in 1..8) { $many += @{ Id = "m$i"; Name = "$g2$i"; Items = @() } }
$many[0] = @{ Id = 'm1'; Name = $g1; Items = $items }
[System.IO.File]::WriteAllText($cfg, (ConvertTo-Json $many -Depth 6), [System.Text.UTF8Encoding]::new($false))
Remove-Item (Join-Path $dir 'shot.log') -Force -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--shot', (Join-Path $shotDir 'final-03-manytabs.png') -PassThru -Wait
Get-Content (Join-Path $dir 'shot.log') | ForEach-Object { "  $_" }
Write-Host ("  exit = {0}" -f $p.ExitCode)

Stop-App
Write-Host ''
Write-Host 'DONE'
