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
#   $PluginRoot, $PluginName, $UpluginPath, $ModuleNames, $PluginBuildDir, $IntDir, $OutDir
# and, for the first module (the only one of a single-module plugin), the per-module variables of Select-PluginModule.
# $ModuleNames is in link order (a module after the plugin modules it links, see PluginModules.txt).
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
    # modules the editor loads on Win64 (not Program/ServerOnly/ClientOnly modules or other platforms)
    $modules = @($descriptor.Modules | Where-Object {
        ($_.Type -match '^(Runtime|RuntimeNoCommandlet|RuntimeAndProgram|Developer|DeveloperTool|Editor|EditorNoCommandlet|EditorAndProgram|UncookedOnly|CookedOnly)$') -and
        (-not $_.PlatformAllowList -or $_.PlatformAllowList -contains 'Win64') -and ($_.PlatformDenyList -notcontains 'Win64') } | ForEach-Object Name)
    if ($modules.Count -eq 0) { throw "$($uplugin[0].Name) declares no editor module for Win64" }

    $script:PluginRoot     = (Resolve-Path $root).Path
    $script:PluginName     = $uplugin[0].BaseName
    $script:UpluginPath    = $uplugin[0].FullName
    $script:PluginBuildDir = Join-Path $script:PluginRoot 'Build'
    $script:IntDir         = if ($env:ARK_PLUGIN_INT_ROOT) { $env:ARK_PLUGIN_INT_ROOT } else { Join-Path $script:PluginBuildDir 'Intermediate' }
    $script:OutDir         = Join-Path $script:PluginBuildDir 'Output'
    $script:IsMultiModule  = $modules.Count -gt 1
    # sources are compiled from a patched copy when the plugin has source patches (see Update-PluginSource)
    $script:SourcePatchDir   = Join-Path $script:PluginBuildDir 'SourcePatches'
    $script:PluginSourceRoot = if (Get-ChildItem $script:SourcePatchDir -Filter *.patch -ErrorAction SilentlyContinue) { Join-Path $script:IntDir 'Source' } else { Join-Path $script:PluginRoot 'Source' }

    # link order: depth-first over the captured plugin module dependencies
    $ordered = [Collections.Generic.List[string]]::new()
    $visit = {
        param($m, $path)
        if ($ordered.Contains($m)) { return }
        if ($path -contains $m) { throw "Cyclic plugin module dependency: $(($path + $m) -join ' -> ')" }
        $deps = Join-Path (Get-ModuleConfigDir $m) 'PluginModules.txt'
        if (Test-Path $deps) { foreach ($d in Get-Content $deps) { if ($d -in $modules) { & $visit $d ($path + $m) } } }
        $ordered.Add($m)
    }
    foreach ($m in $modules) { & $visit $m @() }
    $script:ModuleNames = @($ordered)
    Select-PluginModule $script:ModuleNames[0]
}

# Captured configuration of one module: Build\Config (single-module plugin) or Build\Config\<Module>.
function Get-ModuleConfigDir([string] $Module) {
    $base = Join-Path $script:PluginBuildDir 'Config'
    if ($script:IsMultiModule) { Join-Path $base $Module } else { $base }
}

# Sets the per-module variables in the caller's (script) scope:
#   $ModuleName, $ConfigDir (captured configuration), $ObjDir (objects; Obj or Obj\<Module>)
function Select-PluginModule([string] $Module) {
    $script:ModuleName = $Module
    $script:ConfigDir  = Get-ModuleConfigDir $Module
    $script:ObjDir     = if ($script:IsMultiModule) { Join-Path $script:IntDir "Obj\$Module" } else { Join-Path $script:IntDir 'Obj' }
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
    $p.Replace('$(SDK)', $SdkRoot).Replace('$(PluginSource)', $PluginSourceRoot).Replace('$(PluginGenerated)', $pluginGenerated).Replace('/', '\')
}

# Plugin source patches (<Plugin>\Build\SourcePatches\*.patch, unified diffs relative to the plugin's Source folder):
# adaptations to ARK API differences that cannot be made in the SDK (e.g. vanilla collision channels the devkit
# removed). The plugin folder stays untouched: the sources are copied to <Plugin>\Build\Intermediate\Source and
# patched there; the copy is only refreshed when a source file or a patch changed.
function Update-PluginSource {
    if ($PluginSourceRoot -eq (Join-Path $PluginRoot 'Source')) { return }
    $patches = @(Get-ChildItem $SourcePatchDir -Filter *.patch | Sort-Object Name)
    $files = @(Get-ChildItem (Join-Path $PluginRoot 'Source') -Recurse -File)
    $stamp = (@($patches | ForEach-Object { "$($_.Name):$((Get-FileHash $_.FullName -Algorithm SHA1).Hash)" }) +
              @($files | ForEach-Object { "$($_.FullName):$($_.LastWriteTimeUtc.Ticks)" })) -join "`n"
    $stampFile = Join-Path $IntDir 'Source.stamp'
    if ((Test-Path $stampFile) -and (Get-Content $stampFile -Raw).Trim() -eq $stamp.Trim()) { return }
    Write-Step "Applying $($patches.Count) source patch(es) to a copy of the plugin sources"
    & robocopy (Join-Path $PluginRoot 'Source') $PluginSourceRoot /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    $saved = $env:GIT_CEILING_DIRECTORIES; $env:GIT_CEILING_DIRECTORIES = Split-Path $PluginSourceRoot
    try {
        foreach ($p in $patches) {
            $out = & cmd /c "git -c core.autocrlf=false -C `"$PluginSourceRoot`" apply --verbose --whitespace=nowarn --ignore-whitespace -p1 `"$($p.FullName)`" 2>&1"
            if ($LASTEXITCODE -ne 0 -or ($out -match '^Skipped patch')) { throw "Failed to apply $($p.Name):`n$($out -join "`n")" }
            Write-Host "  applied $($p.Name)"
        }
    } finally { $env:GIT_CEILING_DIRECTORIES = $saved }
    Set-Content $stampFile $stamp
}

# Captured configuration folders of all modules of all plugin folders: Build\Config and Build\Config\<Module>.
function Get-AllModuleConfigDirs {
    foreach ($p in Get-PluginFolders) {
        $cfg = Join-Path $p.FullName 'Build\Config'
        if (Test-Path "$cfg\Definitions.h") { $cfg }
        Get-ChildItem $cfg -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path "$($_.FullName)\Definitions.h" } | ForEach-Object FullName
    }
}

# Union of a per-module config list over all modules of all plugin folders (the SDK serves all plugins).
function Get-AllPluginConfigLines([string] $fileName) {
    $all = foreach ($cfg in Get-AllModuleConfigDirs) {
        $f = Join-Path $cfg $fileName
        if (Test-Path $f) { Get-Content $f }
    }
    @($all | Sort-Object -Unique)
}

# ARK-only classes generated from the devkit PDBs (Config\ArkClasses.txt, Tools\New-ArkClassHeaders.ps1):
# Header (relative to the engine root, file in ArkHeaders\ and the SDK), Module, Includes, Types.
function Get-ArkClasses {
    foreach ($l in Get-Content (Join-Path $SharedConfigDir 'ArkClasses.txt')) {
        $l = $l.Split('#')[0].Trim()
        if (-not $l) { continue }
        $f = $l.Split('|') | ForEach-Object { $_.Trim() }
        [pscustomobject]@{ Header = $f[0]; Module = $f[1]; Includes = @($f[2].Split(';') | Where-Object { $_ }); Types = @($f[3].Split(' ') | Where-Object { $_ }) }
    }
}

# Regenerates SDK\Engine\Source\Runtime\Core\Public\Ark\ArkTypes.h: declarations of the ARK-only types the patched
# SDK headers use (PdbLayout gentypes; force-included through Config\ArkForwardDeclarations.h). Run after the SDK
# headers changed (New-ArkSdk.ps1 patches, Test-ArkAbi.ps1 -Patch).
function Update-ArkTypes {
    $tool = Initialize-PdbLayout
    $out = Join-Path $SdkRoot 'Engine\Source\Runtime\Core\Public\Ark\ArkTypes.h'
    New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
    # every module any plugin links (the patches touch their headers), core modules first
    $order = 'Core', 'CoreUObject', 'Engine'
    $mods = $order + @(Get-AllPluginConfigLines 'EngineModules.txt' | Where-Object { $_ -notin $order })
    $pdbs = $mods | ForEach-Object { Join-Path $DevkitBinaries "$ModulePrefix$_.pdb" } | Where-Object { Test-Path $_ }
    & $tool gentypes $out $SdkRoot (Join-Path $SharedConfigDir 'ArkForwardDeclarations.h') @pdbs
    if ($LASTEXITCODE -ne 0) { throw 'gentypes failed' }
}

# Devkit PDBs of every module any plugin links, lower modules first (a type is taken from the first PDB defining it).
function Get-DevkitPdbs {
    $order = 'Core', 'CoreUObject', 'ApplicationCore', 'InputCore', 'Json', 'SlateCore', 'Slate', 'RenderCore', 'Engine'
    $mods = $order + @(Get-AllPluginConfigLines 'EngineModules.txt' | Where-Object { $_ -notin $order })
    $mods | ForEach-Object { Join-Path $DevkitBinaries "$ModulePrefix$_.pdb" } | Where-Object { Test-Path $_ }
}

function Write-Step([string] $m) { Write-Host "==> $m" -ForegroundColor Cyan }
