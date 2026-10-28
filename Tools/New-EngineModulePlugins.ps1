<#
.SYNOPSIS
  Creates the plugin folders of the engine modules listed in Config\EngineModulePlugins.txt.

.DESCRIPTION
  Some plugins need vanilla UE 5.8 engine modules that the devkit does not ship (ToolsetRegistry loads
  JsonUtilitiesEditor). Each listed module becomes a plugin folder Plugins\<Module>: a copy of the vanilla module
  source in Source\<Module> and a generated <Module>.uplugin. Existing folders are left alone unless -Force.
  Build.ps1 calls this script, so the folders are recreated after a fresh checkout (Plugins\ is not tracked).
.EXAMPLE
  .\Tools\New-EngineModulePlugins.ps1
  .\Tools\New-EngineModulePlugins.ps1 -Force   # refresh the sources after an engine update
#>
param([switch] $Force)
. "$PSScriptRoot\..\Common.ps1"

foreach ($line in Get-Content (Join-Path $SharedConfigDir 'EngineModulePlugins.txt')) {
    if ($line -match '^\s*(#|$)') { continue }
    $module, $path, $type, $phase, $why = $line -split '\s*\|\s*', 5
    $dir = Join-Path $PluginsDir $module
    $uplugin = Join-Path $dir "$module.uplugin"
    if ((Test-Path $uplugin) -and -not $Force) { continue }
    $src = Join-Path $EngineRoot $path
    if (-not (Test-Path $src)) { throw "$module`: $src not found" }
    Write-Step "Creating plugin folder $dir from $src"
    $target = Join-Path $dir "Source\$module"
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item $src $target -Recurse
    Get-ChildItem $target -Recurse -File | ForEach-Object { $_.IsReadOnly = $false }   # engine sources are read-only
    [ordered]@{
        FileVersion   = 3
        Version       = 1
        VersionName   = '1.0'
        FriendlyName  = $module
        Description   = "UE 5.8 engine module $module, which the ARK Devkit does not ship (Config\EngineModulePlugins.txt): $why"
        Category      = 'Other'
        EditorOnly    = $type -like 'Editor*'
        Installed     = $false
        Modules       = @([ordered]@{ Name = $module; Type = $type; LoadingPhase = $phase })
    } | ConvertTo-Json -Depth 5 | Set-Content $uplugin
}
