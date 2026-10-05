# SPDX-FileCopyrightText: 2026 CatalaniDev <catalanidev@gmail.com>
# SPDX-License-Identifier: LicenseRef-MAC-Inspector-1.0
#
# Test-Core.ps1 — automated tests of MAC detection, registry lookup and display texts.
#
# vectors.json holds the results of the browser extension (mac-core.js and i18n.js) for the
# same inputs and the small registry in fixture-oui.json: the Windows version must match them.
# The bundled oui-data.json is checked separately for structure and a few known prefixes.
# Usage: powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Test-Core.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$assemblies = 'System.Windows.Forms', 'System.Drawing', 'UIAutomationClient', 'UIAutomationTypes', 'WindowsBase', 'System.Web.Extensions'
Add-Type -AssemblyName $assemblies
$references = foreach ($name in $assemblies) { [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq $name } | Select-Object -First 1 -ExpandProperty Location }
Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $root 'MacInspector.cs'))) -ReferencedAssemblies ($references + 'System.dll', 'System.Core.dll') -Language CSharp

$js = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$js.MaxJsonLength = [int]::MaxValue
$vectors = $js.DeserializeObject([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'vectors.json'), [Text.Encoding]::UTF8))
$failures = New-Object System.Collections.ArrayList
$checks = 0

# MAC detection in text, including inputs that must be rejected and long adversarial strings
foreach ($t in $vectors['texts']) {
  $checks++
  $timer = [Diagnostics.Stopwatch]::StartNew()
  $got = @([MacInspector.Core]::FindMacs($t['text']) | ForEach-Object { "$($_.Mac)@$($_.Index)" }) -join ' '
  $want = @($t['found'] | ForEach-Object { "$($_[0])@$($_[1])" }) -join ' '
  $label = $t['text'].Substring(0, [Math]::Min(50, $t['text'].Length))
  if ($got -ne $want) { [void]$failures.Add("detection [$label]: expected [$want], got [$got]") }
  if ($timer.ElapsedMilliseconds -gt 500) { [void]$failures.Add("detection [$label] took $($timer.ElapsedMilliseconds) ms") }
}

# Lookup and texts, in English and Italian
$fixture = [MacInspector.OuiRegistry]::Load((Join-Path $PSScriptRoot 'fixture-oui.json'))
foreach ($l in $vectors['lookups']) {
  $checks++
  [MacInspector.Texts]::Lang = $l['lang']
  $v = [MacInspector.Core]::Describe($fixture.Lookup($l['input']))
  foreach ($field in 'Kind', 'Category', 'Title', 'Detail') {
    $got = [string]$v.$field; $want = [string]$l[$field.ToLower()]
    if ($got -ne $want) { [void]$failures.Add("lookup $($l['lang']) [$($l['input'])] $field`: expected [$want], got [$got]"); break }
  }
}

# The bundled registry
$checks++
$registry = [MacInspector.OuiRegistry]::Load((Join-Path $root 'oui-data.json'))
[MacInspector.Texts]::Lang = 'en'
if ($registry.Total -lt 50000) { [void]$failures.Add("oui-data.json: only $($registry.Total) prefixes") }
foreach ($known in @(@('00:1C:0E:00:00:00', 'Cisco'), @('B8:27:EB:00:00:00', 'Raspberry'))) {
  $checks++
  $title = [MacInspector.Core]::Describe($registry.Lookup($known[0])).Title
  if ($title -notmatch $known[1]) { [void]$failures.Add("oui-data.json: $($known[0]) is [$title], expected $($known[1])") }
}

if ($failures.Count) {
  $failures | ForEach-Object { "FAIL $_" }
  "`n$($failures.Count) of $checks checks failed"
  exit 1
}
"All $checks checks passed ($($vectors['texts'].Count) texts, $($vectors['lookups'].Count) lookups, bundled registry with $($registry.Total) prefixes)"
