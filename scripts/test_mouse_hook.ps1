$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$hostBinary = Join-Path $project 'bin\nexuswpp.exe'
if (-not (Test-Path -LiteralPath $hostBinary)) { throw 'Run compile.ps1 first.' }
$testDir = Join-Path $project 'bin\mouse-hook-tests'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
$testBinary = Join-Path $testDir 'MouseHookRegression.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe ("/out:" + $testBinary) /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'tests\MouseHookRegression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Mouse hook regression compilation failed.' }
& $testBinary $hostBinary
if ($LASTEXITCODE -ne 0) { throw 'Mouse hook regression failed.' }
