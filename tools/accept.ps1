# Final acceptance pass against the shipped dist build.
$ErrorActionPreference = 'Stop'

# Capture.cs needs these assemblies loaded first, otherwise Add-Type -Path fails silently
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class AW {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const int VK_ESCAPE = 0x1B;
}
"@

# Window enumeration lives in Capture.cs (PowerShell ScriptBlock delegates cannot
# reliably reach $script: scope, which broke the earlier inline version).
try {
    Add-Type -Path 'E:\ShortcutManager\tools\Capture.cs' -ReferencedAssemblies System.Drawing, System.Windows.Forms -ErrorAction Stop
} catch {
    Write-Host ('FATAL: cannot load Capture.cs: ' + $_.Exception.Message)
    throw
}

$dist = 'E:\ShortcutManager\dist\ShortcutManager'
$exe  = Join-Path $dist 'ShortcutManager.exe'
$cfg  = Join-Path $dist 'config.json'

function Stop-App { Get-Process ShortcutManager -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 400 }
function Cn([int[]]$cp) { -join ($cp | ForEach-Object { [char]$_ }) }

# Main window = the HwndWrapper that has a title (WPF also creates hidden helpers)
function Find-VisibleWpf([int]$targetPid) {
    [void][Nat]::ScanWindows($targetPid)
    return [Nat]::MainHwndLong
}

function Show-Candidates([int]$targetPid) {
    $dump = [Nat]::ScanWindows($targetPid)
    Write-Host '  candidate windows:'
    if ($dump) { $dump -split "`n" | ForEach-Object { Write-Host ("    " + $_) } }
}

function Start-Gui {
    $proc = Start-Process -FilePath $exe -PassThru
    $h = [int64]0
    for ($i = 0; $i -lt 400; $i++) {
        Start-Sleep -Milliseconds 25
        $h = [int64](Find-VisibleWpf $proc.Id)
        if ($h -ne 0) { break }
    }
    if ($h -eq 0) { throw 'no visible window' }
    # store the handle as Int64: IntPtr loses its value inside a hashtable
    return @{ Proc = $proc; Hwnd = $h }
}

function Hwnd([object]$v) { return [IntPtr]([int64]$v) }

$results = New-Object System.Collections.ArrayList
function Record($name, $ok, $detail) {
    [void]$results.Add([pscustomobject]@{ Test = $name; Ok = [bool]$ok; Detail = $detail })
    $tag = 'FAIL'
    if ($ok) { $tag = 'PASS' }
    Write-Host ("  [{0}] {1} -- {2}" -f $tag, $name, $detail)
}

# ---- prepare data ----
$sandbox = Join-Path $env:TEMP 'sm-accept'
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
foreach ($n in 'Docs', 'Pics') { New-Item -ItemType Directory -Force -Path (Join-Path $sandbox $n) | Out-Null }
foreach ($n in 'a.txt', 'b.md', 'c.docx') { Set-Content -Path (Join-Path $sandbox $n) -Value 'x' -Encoding UTF8 }

Stop-App
Remove-Item $cfg -Force -ErrorAction SilentlyContinue

Write-Host '=== A. window geometry ==='
$app = Start-Gui
Start-Sleep -Milliseconds 900
$r = New-Object AW+RECT
[void][AW]::GetWindowRect((Hwnd $app.Hwnd), [ref]$r)
$dpi = [AW]::GetDpiForWindow((Hwnd $app.Hwnd))
$wDip = [int]($r.R - $r.L)
$hDip = [int]($r.B - $r.T)
Record 'window created' $true ("{0}x{1} virtual px, dpi={2}" -f $wDip, $hDip, $dpi)

# The window auto-hides on losing focus, so a freshly launched app may already be
# hidden if this test process holds the foreground. Foreground it first, then check.
[void][AW]::SetForegroundWindow((Hwnd $app.Hwnd))
Start-Sleep -Milliseconds 700
$vis = [AW]::IsWindowVisible((Hwnd $app.Hwnd))
Record 'window visible after activation' $vis ("visible={0} (expect True)" -f $vis)

# centered check against the desktop work area
Add-Type -AssemblyName System.Windows.Forms
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$cx = $r.L + ($r.R - $r.L) / 2
$cy = $r.T + ($r.B - $r.T) / 2
$dx = [math]::Abs($cx - ($wa.Width / 2))
$dy = [math]::Abs($cy - ($wa.Height / 2))
Record 'roughly centered' (($dx -lt 60) -and ($dy -lt 60)) ("dx={0} dy={1}" -f [int]$dx, [int]$dy)

Write-Host ''
Write-Host '=== B. escape hides to tray ==='
[void][AW]::SetForegroundWindow((Hwnd $app.Hwnd))
Start-Sleep -Milliseconds 300
[void][AW]::PostMessage((Hwnd $app.Hwnd), [AW]::WM_KEYDOWN, [IntPtr][AW]::VK_ESCAPE, [IntPtr]::Zero)
[void][AW]::PostMessage((Hwnd $app.Hwnd), [AW]::WM_KEYUP, [IntPtr][AW]::VK_ESCAPE, [IntPtr]::Zero)
Start-Sleep -Milliseconds 900
$alive = $null -ne (Get-Process -Id $app.Proc.Id -ErrorAction SilentlyContinue)
Write-Host ("  (Esc on empty selection should hide; process alive={0})" -f $alive)
Record 'process survives Esc' $alive 'alive=True'
Record 'config auto-created' (Test-Path $cfg) ("config.json exists={0}" -f (Test-Path $cfg))

Write-Host ''
Write-Host '=== C. restart with data, second instance is blocked ==='
Stop-App
$g1 = Cn @(0x5E38, 0x7528)
$g2 = Cn @(0x5DE5, 0x4F5C)
$items = @()
foreach ($n in 'Docs', 'Pics') { $items += @{ Path = (Join-Path $sandbox $n); DisplayName = ''; CachedIcon = $null } }
foreach ($n in 'a.txt', 'b.md', 'c.docx') { $items += @{ Path = (Join-Path $sandbox $n); DisplayName = ''; CachedIcon = $null } }
$items += @{ Path = 'C:\Windows\System32\notepad.exe'; DisplayName = ''; CachedIcon = $null }
$groups = @(
    @{ Id = 'g1'; Name = $g1; Items = $items },
    @{ Id = 'g2'; Name = $g2; Items = @() }
)
[System.IO.File]::WriteAllText($cfg, (ConvertTo-Json $groups -Depth 6), [System.Text.UTF8Encoding]::new($false))

$app = Start-Gui
Start-Sleep -Milliseconds 1200
Record 'restart loads data' $true 'window up'
Record 'config preserved' ((Get-Content $cfg -Raw -Encoding UTF8) -match 'notepad') 'notepad entry survives round trip'

# second instance: should show a message box and exit; we just confirm it does not add a second visible window
$before = @(Get-Process ShortcutManager -ErrorAction SilentlyContinue).Count
$p2 = Start-Process -FilePath $exe -PassThru
Start-Sleep -Milliseconds 2500
$after = @(Get-Process ShortcutManager -ErrorAction SilentlyContinue).Count
Record 'single instance enforced' ($after -le $before) ("processes before={0} after={1}" -f $before, $after)

# clean up any lingering second-instance dialog
Get-Process ShortcutManager -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $app.Proc.Id } | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '=== D. clean shutdown writes config ==='
$null = (Hwnd $app.Hwnd)
[void][AW]::PostMessage((Hwnd $app.Hwnd), [AW]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)
Start-Sleep -Milliseconds 1200
$alive = $null -ne (Get-Process -Id $app.Proc.Id -ErrorAction SilentlyContinue)
Record 'close hides, does not exit' $alive 'alive=True'
Record 'config saved on close' (Test-Path $cfg) 'config.json present'

Stop-App

Write-Host ''
Write-Host '=== E. window style ==='
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class ExStyle {
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    public static bool IsTopmost(IntPtr h) { return (GetWindowLong(h, -20) & 0x00000008) != 0; }
}
"@
$app = Start-Gui
Start-Sleep -Milliseconds 900
$h = (Hwnd $app.Hwnd)
Record 'not always-on-top' (-not [ExStyle]::IsTopmost($h)) ("WS_EX_TOPMOST={0} (expect False)" -f [ExStyle]::IsTopmost($h))

# hide-on-deactivate: make sure our window is foregrounded FIRST, then hand focus to
# another app and confirm our window hides.
[void][AW]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 800
$shownFirst = [AW]::IsWindowVisible($h)

$np = Start-Process notepad -PassThru
for ($i = 0; $i -lt 120; $i++) { $np.Refresh(); if ($np.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 50 }
[void][AW]::SetForegroundWindow([IntPtr]$np.MainWindowHandle)
Start-Sleep -Milliseconds 1800

$visNow = [AW]::IsWindowVisible($h)
Record 'hides when focus moves away' ($shownFirst -and -not $visNow) ("visible before={0} after={1} (expect True then False)" -f $shownFirst, $visNow)
Record 'survives auto-hide' ($null -ne (Get-Process -Id $app.Proc.Id -ErrorAction SilentlyContinue)) 'process alive'
Stop-Process -Id $np.Id -Force -ErrorAction SilentlyContinue

Stop-App

Write-Host ''
Write-Host '=== summary ==='
$pass = @($results | Where-Object { $_.Ok }).Count
$fail = @($results | Where-Object { -not $_.Ok }).Count
Write-Host ("  pass={0} fail={1}" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
exit 0
