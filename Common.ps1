# Shared settings and helpers of the ARK Devkit plugin build system. Dot-source this file.
#
# Layout:  <root>\                         build system, ArkPatches, shared Config, SDK, shared Intermediate (caches)
#          <root>\Plugins\<Plugin>\        one folder per plugin: <Name>.uplugin, Source\, Resources\, ...
#          <root>\Plugins\<Plugin>\Build\  Config\ (captured build environment), Intermediate\, Output\
# Scripts that work on one plugin take the plugin folder name and call Initialize-Plugin.
$ErrorActionPreference = 'Stop'

$BuildDir        = $PSScriptRoot                         # root of the build system (PluginBuild)
$PluginsDir      = Join-Path $BuildDir 'Plugins'         # one sub folder per plugin
$SharedConfigDir = Join-Path $BuildDir 'Config'          # ArkForwardDeclarations.h, AbiExclusions.txt, SdkHeaders.Extra.txt
$SharedIntDir    = Join-Path $BuildDir 'Intermediate'    # PDB text cache, engine-only UHT run

# --- user configurable paths (override via environment variables) --------------------------------------------
# These are the only machine specific paths of the build system; every script and tool gets them from here
# (the C# and bash tools through the environment variables set by Initialize-PdbLayout / Invoke-Bash).
$EngineRoot  = if ($env:ARK_UE_ROOT) { $env:ARK_UE_ROOT } else { 'D:\ASA\UE_5.8' }               # vanilla UE 5.8 (headers, UBT/UHT)
$DevkitRoot  = if ($env:ARK_DEVKIT_ROOT) { $env:ARK_DEVKIT_ROOT } else { 'D:\ASA\ARKDevkitBeta' } # ARK Devkit (read only)
$SdkRoot     = if ($env:ARK_SDK_ROOT) { $env:ARK_SDK_ROOT } else { Join-Path $BuildDir 'SDK' }     # generated ARK SDK (shared)
$PdbCacheDir = if ($env:ARK_PDB_CACHE) { $env:ARK_PDB_CACHE } else { Join-Path $SharedIntDir 'PdbCache' }  # PDB text dumps (~12 GB)
# Tools found automatically: Visual Studio via vswhere (ARK_MSVC_VERSION / ARK_WINSDK_VERSION pick versions),
# llvm-pdbutil from the VS LLVM component or PATH (ARK_LLVM_PDBUTIL), bash from PATH or Git for Windows (ARK_BASH).

$DevkitBinaries = Join-Path $DevkitRoot 'Engine\Binaries\Win64'
$ModulePrefix   = 'ShooterGameEditor-'
$UbtExe         = Join-Path $EngineRoot 'Engine\Binaries\DotNET\UnrealBuildTool\UnrealBuildTool.exe'
$PdbLayoutExe   = Join-Path $BuildDir 'Tools\bin\PdbLayout\PdbLayout.exe'   # built from Tools\PdbLayout on first use

# Plugin folders: sub folders of Plugins\ that contain a .uplugin file.
function Get-PluginFolders {
    Get-ChildItem $PluginsDir -Directory -ErrorAction SilentlyContinue | Where-Object { Get-ChildItem $_.FullName -Filter *.uplugin -File -ErrorAction SilentlyContinue }
}

# Selects the plugin to work on and sets the per-plugin variables in the caller's (script) scope:
#   $PluginRoot, $PluginName, $UpluginPath, $ModuleName, $PluginBuildDir, $ConfigDir, $IntDir, $OutDir
function Initialize-Plugin([string] $Plugin) {
    if (-not $Plugin) {
        $names = (Get-PluginFolders).Name -join ', '
        throw "Specify the plugin folder (one of: $names)"
    }
    $root = if ([IO.Path]::IsPathRooted($Plugin)) { $Plugin } else { Join-Path $PluginsDir $Plugin }
    if (-not (Test-Path $root)) { throw "Plugin folder not found: $root" }
    $uplugin = @(Get-ChildItem $root -Filter *.uplugin -File)
    if ($uplugin.Count -ne 1) { throw "Expected exactly one .uplugin file in $root" }
    $descriptor = Get-Content $uplugin[0].FullName -Raw | ConvertFrom-Json
    $modules = @($descriptor.Modules | Where-Object { $_.Type -like 'Editor*' -or $_.Type -eq 'Runtime' } | ForEach-Object Name)
    if ($modules.Count -ne 1) { throw "$($uplugin[0].Name) declares $($modules.Count) modules; only single-module plugins are supported (see EXTENDING.md)" }

    $script:PluginRoot     = (Resolve-Path $root).Path
    $script:PluginName     = $uplugin[0].BaseName
    $script:UpluginPath    = $uplugin[0].FullName
    $script:ModuleName     = $modules[0]
    $script:PluginBuildDir = Join-Path $script:PluginRoot 'Build'
    $script:ConfigDir      = Join-Path $script:PluginBuildDir 'Config'
    $script:IntDir         = if ($env:ARK_PLUGIN_INT_ROOT) { $env:ARK_PLUGIN_INT_ROOT } else { Join-Path $script:PluginBuildDir 'Intermediate' }
    $script:OutDir         = Join-Path $script:PluginBuildDir 'Output'
}

function Get-VsInstallPath {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vs = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath }
    if (-not $vs) { throw 'Visual Studio with the MSVC x64 toolset not found' }
    $vs
}

function Find-Msvc {
    $vs = Get-VsInstallPath
    $ver = if ($env:ARK_MSVC_VERSION) { $env:ARK_MSVC_VERSION } else { (Get-Content "$vs\VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt").Trim() }
    $tools = "$vs\VC\Tools\MSVC\$ver"
    $kitRoot = "${env:ProgramFiles(x86)}\Windows Kits\10"
    $kitVer = if ($env:ARK_WINSDK_VERSION) { $env:ARK_WINSDK_VERSION } else { (Get-ChildItem "$kitRoot\Include" -Directory | Where-Object { Test-Path "$($_.FullName)\um\windows.h" } | Sort-Object { [version]$_.Name } | Select-Object -Last 1).Name }
    [pscustomobject]@{
        Bin      = "$tools\bin\Hostx64\x64"
        Cl       = "$tools\bin\Hostx64\x64\cl.exe"
        Link     = "$tools\bin\Hostx64\x64\link.exe"
        Includes = @("$tools\include", "$kitRoot\Include\$kitVer\ucrt", "$kitRoot\Include\$kitVer\shared", "$kitRoot\Include\$kitVer\um", "$kitRoot\Include\$kitVer\winrt")
        LibPaths = @("$tools\lib\x64", "$kitRoot\Lib\$kitVer\ucrt\x64", "$kitRoot\Lib\$kitVer\um\x64")
        Version  = $ver
    }
}

function Find-LlvmPdbUtil {
    if ($env:ARK_LLVM_PDBUTIL) { return $env:ARK_LLVM_PDBUTIL }
    $vsTool = Join-Path (Get-VsInstallPath) 'VC\Tools\Llvm\x64\bin\llvm-pdbutil.exe'   # VS component "C++ Clang tools"
    if (Test-Path $vsTool) { return $vsTool }
    $onPath = Get-Command llvm-pdbutil -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw 'llvm-pdbutil not found: install the Visual Studio component "C++ Clang tools for Windows" or set ARK_LLVM_PDBUTIL'
}

function Find-Bash {
    if ($env:ARK_BASH) { return $env:ARK_BASH }
    $onPath = Get-Command bash -ErrorAction SilentlyContinue | Where-Object { $_.Source -notlike '*\System32\*' }   # not the WSL launcher
    if ($onPath) { return $onPath.Source }
    $git = Join-Path $env:ProgramFiles 'Git\bin\bash.exe'
    if (Test-Path $git) { return $git }
    throw 'bash not found: install Git for Windows or set ARK_BASH'
}

# Runs one of the bash tools in Tools\ with the configured paths in its environment.
function Invoke-Bash([string] $script, [string[]] $arguments) {
    $saved = $env:ARK_SDK_ROOT
    $env:ARK_SDK_ROOT = $SdkRoot
    $env:LLVM_PDBUTIL = Find-LlvmPdbUtil
    try { & (Find-Bash) (Join-Path $BuildDir "Tools\$script") @arguments }
    finally { $env:ARK_SDK_ROOT = $saved }
}

# Builds Tools\PdbLayout if needed and passes the configured paths to it; returns the path of PdbLayout.exe.
function Initialize-PdbLayout {
    if (-not (Test-Path $PdbLayoutExe)) {
        & dotnet build -c Release (Join-Path $BuildDir 'Tools\PdbLayout') -o (Split-Path $PdbLayoutExe) | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'building Tools\PdbLayout failed (.NET SDK 9 required)' }
    }
    $env:PDBLAYOUT_CACHE = $PdbCacheDir
    $env:LLVM_PDBUTIL = Find-LlvmPdbUtil
    $PdbLayoutExe
}

function Expand-ConfigPath([string] $p, [string] $pluginGenerated) {
    $p.Replace('$(SDK)', $SdkRoot).Replace('$(PluginSource)', (Join-Path $PluginRoot 'Source')).Replace('$(PluginGenerated)', $pluginGenerated).Replace('/', '\')
}

# Union of a per-plugin config list over all plugin folders (the SDK serves all plugins).
function Get-AllPluginConfigLines([string] $fileName) {
    $all = foreach ($p in Get-PluginFolders) {
        $f = Join-Path $p.FullName "Build\Config\$fileName"
        if (Test-Path $f) { Get-Content $f }
    }
    @($all | Sort-Object -Unique)
}

function Write-Step([string] $m) { Write-Host "==> $m" -ForegroundColor Cyan }
