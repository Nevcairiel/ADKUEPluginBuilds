<#
.SYNOPSIS
  Load test of the built plugin DLL against the devkit binaries.
.DESCRIPTION
  Builds Tools\LoadTest\LoadTest.cpp and uses it to load the plugin's ShooterGameEditor-<Module>.dll with the
  devkit's binaries directory as DLL search path. The Windows loader resolves every import against the real
  ShooterGameEditor-*.dll files and the module's static initialization runs. The devkit is not modified.
.EXAMPLE
  .\Tools\Test-DllLoad.ps1 BlueprintAssist
#>
param([Parameter(Position = 0)] [string] $Plugin)   # plugin folder name below Plugins
. "$PSScriptRoot\..\Common.ps1"
Initialize-Plugin $Plugin
$dlls = @($ModuleNames | ForEach-Object { Join-Path $OutDir "$PluginName\Binaries\Win64\$ModulePrefix$_.dll" })   # link order
foreach ($dll in $dlls) { if (-not (Test-Path $dll)) { throw "Build the plugin first ($dll missing)" } }

$msvc = Find-Msvc
$exe = Join-Path $IntDir 'LoadTest\LoadTest.exe'
New-Item -ItemType Directory -Force (Split-Path $exe) | Out-Null
$inc = ($msvc.Includes | ForEach-Object { "/I`"$_`"" }) -join ' '
$lib = ($msvc.LibPaths | ForEach-Object { "/LIBPATH:`"$_`"" }) -join ' '
cmd /c "`"$($msvc.Cl)`" /nologo /EHsc /O2 $inc `"$PSScriptRoot\LoadTest\LoadTest.cpp`" /Fo`"$(Split-Path $exe)\\`" /Fe`"$exe`" /link $lib" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'building LoadTest.exe failed' }

& $exe $DevkitBinaries @dlls
$code = $LASTEXITCODE
if ($code -ne 0) { throw ("Load test failed (exit code 0x{0:X8})" -f $code) }
Write-Host "OK: the plugin DLL$(if ($dlls.Count -gt 1) { 's' }) load$(if ($dlls.Count -eq 1) { 's' }) against the devkit binaries" -ForegroundColor Green
