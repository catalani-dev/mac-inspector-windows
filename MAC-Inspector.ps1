# SPDX-FileCopyrightText: 2026 CatalaniDev <catalanidev@gmail.com>
# SPDX-License-Identifier: LicenseRef-MAC-Inspector-1.0
#
# MAC-Inspector.ps1 — starts MAC Inspector for Windows.
#
# Compiles MacInspector.cs with the C# compiler built into Windows and runs it in the
# notification area. Needs only Windows PowerShell 5.1, which ships with Windows 10 and 11.
# Usage: run Start-MAC-Inspector.cmd, or
#   powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File MAC-Inspector.ps1 [-Exit]
# -Exit closes the running instance (for scripts and updates).

param([switch]$Exit)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

# Windows Forms needs a single-threaded apartment: restart in STA mode if necessary
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
  $relaunch = @('-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
  if ($Exit) { $relaunch += '-Exit' }
  Start-Process powershell.exe -WindowStyle Hidden -ArgumentList $relaunch
  return
}

$assemblies = 'System.Windows.Forms', 'System.Drawing', 'UIAutomationClient', 'UIAutomationTypes', 'WindowsBase', 'System.Web.Extensions'
try {
  Add-Type -AssemblyName $assemblies
  if (-not ('MacInspector.Program' -as [type])) {
    $references = foreach ($name in $assemblies) { [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq $name } | Select-Object -First 1 -ExpandProperty Location }
    $source = [IO.File]::ReadAllText((Join-Path $here 'MacInspector.cs'))
    Add-Type -TypeDefinition $source -ReferencedAssemblies ($references + 'System.dll', 'System.Core.dll') -Language CSharp
  }
  if ($Exit) { [void][MacInspector.Program]::Exit(); return }
  [MacInspector.Program]::Run($here)
} catch {
  Add-Type -AssemblyName System.Windows.Forms
  [System.Windows.Forms.MessageBox]::Show("MAC Inspector could not start:`n`n$($_.Exception.Message)", 'MAC Inspector', 'OK', 'Error') | Out-Null
  exit 1
}
