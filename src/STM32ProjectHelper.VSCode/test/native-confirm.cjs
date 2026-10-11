'use strict';
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');

// Only confirms the dialog belonging to the isolated test profile.
async function confirmWrite() {
  const script = `Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IsolatedDialogInput {
 [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle,out uint process);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
}
'@
$instance = Get-CimInstance Win32_Process -Filter "Name = 'Code.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($env:STM32_SMOKE_PROFILE) -and $_.CommandLine -notmatch '--type=' } | Select-Object -First 1
if (-not $instance) { throw 'Isolated VS Code window not found.' }
$processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$instance.ProcessId)
$nameCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '确认写入')
$deadline = [DateTime]::UtcNow.AddSeconds(45)
while ([DateTime]::UtcNow -lt $deadline) {
  $windows = New-Object 'System.Collections.Generic.List[System.Windows.Automation.AutomationElement]'
  $handle = (Get-Process -Id $instance.ProcessId).MainWindowHandle
  if ($handle -ne [IntPtr]::Zero) { $windows.Add([System.Windows.Automation.AutomationElement]::FromHandle($handle)) }
  $owned = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)
  foreach ($window in $owned) { $windows.Add($window) }
  foreach ($window in $windows) {
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
    foreach ($button in $buttons) {
      if ($button.Current.IsEnabled -and -not $button.Current.IsOffscreen) {
        $pattern = $null
        if ($button.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) { $pattern.Invoke() }
        else {
          if ($button.Current.ClassName -ne 'CCPushButton' -or $button.Current.ProcessId -ne $instance.ProcessId) { continue }
          [void][IsolatedDialogInput]::SetForegroundWindow($handle)
          Start-Sleep -Milliseconds 100
          $foregroundProcess = [uint32]0
          [void][IsolatedDialogInput]::GetWindowThreadProcessId([IsolatedDialogInput]::GetForegroundWindow(),[ref]$foregroundProcess)
          if ($foregroundProcess -ne $instance.ProcessId) { throw 'Isolated confirmation window is not foreground.' }
          $point = $button.GetClickablePoint()
          $previous = New-Object IsolatedDialogInput+POINT
          [void][IsolatedDialogInput]::GetCursorPos([ref]$previous)
          try {
            [void][IsolatedDialogInput]::SetCursorPos([int]$point.X,[int]$point.Y)
            [IsolatedDialogInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
            [IsolatedDialogInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
          } finally { [void][IsolatedDialogInput]::SetCursorPos($previous.X,$previous.Y) }
        }
        Write-Output 'Isolated native write dialog confirmed.'
        exit 0
      }
    }
  }
  Start-Sleep -Milliseconds 100
}
throw 'Native write confirmation dialog was not found.'`;
  return promisify(execFile)('powershell.exe', ['-NoProfile', '-Command', script], { windowsHide: true, timeout: 55000 });
}

module.exports = { confirmWrite };
