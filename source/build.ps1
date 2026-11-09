$ErrorActionPreference = 'Stop'
$taskSource = $PSScriptRoot
$taskOutput = Split-Path -Parent $taskSource
$taskCode   = Join-Path $taskSource 'G305Battery.cs'
$framework  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $framework)) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $framework)) { throw '.NET Framework C# compiler not found.' }

$taskRefs = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll')
$taskIcon = Join-Path $taskSource 'app.ico'
if (-not (Test-Path $taskIcon)) { throw 'source\app.ico is missing. Regenerate it with: python source\make_logo.py' }
$taskOpts = @('/nologo','/utf8output','/codepage:65001','/optimize+','/platform:anycpu',"/win32icon:$taskIcon")

& $framework @taskOpts /target:winexe "/out:$taskOutput\G305Battery.exe" @taskRefs $taskCode
if ($LASTEXITCODE -ne 0) { throw 'Tray build failed.' }
& $framework @taskOpts /target:exe "/out:$taskOutput\G305Battery-check.exe" @taskRefs $taskCode
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic build failed.' }

Write-Host "Built: $taskOutput\G305Battery.exe and G305Battery-check.exe (icon: $taskIcon)"
