# -AllowSingleDisplay: when Windows has exactly one display (the laptop panel with the external
# monitor unplugged) the test window may use it; with two or more, the primary stays forbidden.
param([int]$ProcessId = 0, [switch]$Once, [switch]$AllowSingleDisplay)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class E2EWindow {
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
}
'@
[void][E2EWindow]::SetThreadDpiAwarenessContext([IntPtr](-4))
function Get-Probe {
  $screens = @([System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
    @{ name=$_.DeviceName; primary=$_.Primary; left=$_.Bounds.Left; top=$_.Bounds.Top; right=$_.Bounds.Right; bottom=$_.Bounds.Bottom }
  })
  $result = @{ at=[DateTimeOffset]::UtcNow.ToString('o'); screens=$screens }
  if ($ProcessId -eq 0) {
    $result.existing = @(Get-Process projmap -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
  } else {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    $result.alive = $null -ne $proc
    if ($proc) {
      $result.responding = $proc.Responding
      $result.handle = $proc.MainWindowHandle.ToInt64()
      # Keep the WebView browser roots identifiable even if the host later crashes.
      $result.children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" | ForEach-Object {
        @{ pid=$_.ProcessId; created=$_.CreationDate.ToUniversalTime().ToString('o') }
      })
      if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
        $rect = New-Object E2EWindow+Rect
        if ([E2EWindow]::GetWindowRect($proc.MainWindowHandle, [ref]$rect)) {
          $result.rect = @{ left=$rect.Left; top=$rect.Top; right=$rect.Right; bottom=$rect.Bottom }
          $overlap = @($screens | Where-Object { $rect.Left -lt $_.right -and $rect.Right -gt $_.left -and $rect.Top -lt $_.bottom -and $rect.Bottom -gt $_.top })
          $single = $AllowSingleDisplay -and $screens.Count -eq 1
          $result.safe = $overlap.Count -gt 0 -and ($single -or @($overlap | Where-Object primary).Count -eq 0)
        }
      }
    }
  }
  $result | ConvertTo-Json -Depth 5 -Compress
}
do {
  Get-Probe
  if ($Once) { break }
  Start-Sleep -Milliseconds 1000
} while ($true)
