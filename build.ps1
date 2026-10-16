# Builds app\Chop.exe (and app\ChopCli.exe, a console build for scripted exports) with the
# C# compiler that ships with Windows (.NET Framework 4.8) - no SDK needed.
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'src'
$app = Join-Path $PSScriptRoot 'app'
$ico = Join-Path $src 'chop.ico'
if (-not (Test-Path $ico)) { & (Join-Path $src 'make-icon.ps1') -Out $ico }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$files = (Get-ChildItem $src -Filter *.cs).FullName
$common = @('-nologo', '-platform:x64', '-optimize+', '-codepage:65001', "-win32icon:$ico",
    '-r:System.Web.Extensions.dll', '-r:System.Windows.Forms.dll', '-r:System.Drawing.dll')
& $csc @common -target:winexe "-out:$app\Chop.exe" @files
if ($LASTEXITCODE -ne 0) { throw 'Chop.exe build failed' }
& $csc @common -target:exe "-out:$app\ChopCli.exe" @files
if ($LASTEXITCODE -ne 0) { throw 'ChopCli.exe build failed' }
Write-Host "Built $app\Chop.exe"
