<#
.SYNOPSIS
  One-time capture of the build environment UBT uses for a plugin against vanilla UE 5.8.

.DESCRIPTION
  Builds the plugin once with the stock UnrealBuildTool against the vanilla UE 5.8 tree (host project in
  <Plugin>\Build\Intermediate\UbtBaseline), then converts UBT's response files, Definitions.h, dependency files and
  the UHT manifest into the engine-independent configuration files under <Plugin>\Build\Config used by Build.ps1:

    IncludePaths.txt     include directories, rooted at $(SDK) / $(PluginSource) / $(PluginGenerated)
    CompilerFlags.rsp    cl.exe flags (no include paths, no outputs)
    LinkerFlags.rsp      link.exe flags (no inputs, no outputs)
    SystemLibs.txt       Windows import libraries
    ThirdPartyLibs.txt   vendor libraries of engine ThirdParty modules (relative to the engine root, copied into the SDK)
    EngineModules.txt    engine modules the plugin links against (-> ShooterGameEditor-<Module>.lib)
    PluginModules.txt    other modules of the same plugin this module links against (multi-module plugins)
    Definitions.h        UBT's per-module definitions, retargeted to the ShooterGameEditor target
    SdkHeaders.txt       engine headers to copy into the SDK (relative to the engine root)
    UhtModules.json      UHT manifest entries of all modules the plugin depends on (relative paths)

  A multi-module plugin gets one such set per module in <Plugin>\Build\Config\<Module>.
  Only needs to run again if the plugin's sources/dependencies or the engine version change.
.EXAMPLE
  .\Tools\Capture-UbtBaseline.ps1 BlueprintAssist
#>
param(
    [Parameter(Position = 0)] [string] $Plugin,   # plugin folder name below Plugins
    [switch] $SkipBuild
)
. "$PSScriptRoot\..\Common.ps1"
Initialize-Plugin $Plugin
$hostDir   = Join-Path $IntDir 'UbtBaseline'
New-Item -ItemType Directory -Force $hostDir | Out-Null

# --- 1. host project with a junction to the plugin sources ------------------------------------------------
$hostPlugin = Join-Path $hostDir "Plugins\$PluginName"
New-Item -ItemType Directory -Force $hostPlugin | Out-Null
Copy-Item $UpluginPath $hostPlugin -Force
Set-Content "$hostDir\Host.uproject" "{ `"FileVersion`": 3, `"Plugins`": [ { `"Name`": `"$PluginName`", `"Enabled`": true } ] }"
# Link only the modules the plugin descriptor declares: Source may contain other plugins' modules
# (e.g. AutoSizeComments), which UBT would reject inside this plugin.
$hostSource = Join-Path $hostPlugin 'Source'
if ((Test-Path $hostSource) -and (Get-Item $hostSource).LinkType -eq 'Junction') { cmd /c rmdir "$hostSource" | Out-Null }
New-Item -ItemType Directory -Force $hostSource | Out-Null
$declared = (Get-Content $UpluginPath -Raw | ConvertFrom-Json).Modules.Name
foreach ($m in $declared) {
    if (-not (Test-Path "$hostSource\$m")) { New-Item -ItemType Junction -Path "$hostSource\$m" -Target "$PluginRoot\Source\$m" | Out-Null }
}
if (-not $SkipBuild) {
    & $UbtExe UnrealEditor Win64 Development `
        "-Project=$hostDir\Host.uproject" "-Plugin=$hostPlugin\$PluginName.uplugin" -NoHotReload -NoUBA -WaitMutex
    if ($LASTEXITCODE -ne 0) { throw "UBT baseline build failed" }
}

foreach ($module in $ModuleNames) {
Select-PluginModule $module
$configDir = $ConfigDir
New-Item -ItemType Directory -Force $configDir | Out-Null
Write-Step "Module $ModuleName -> $configDir"
$int = Join-Path $hostPlugin "Intermediate\Build\Win64\x64\UnrealEditor\Development\$ModuleName"
$engineSource = (Join-Path $EngineRoot 'Engine\Source').Replace('\', '/')
$hostPluginFwd = $hostPlugin.ToString().Replace('\', '/')

function Convert-IncludePath([string] $p) {
    $p = $p.Replace('\', '/')
    if ($p -eq "$hostPluginFwd/Source" -or $p -like "$hostPluginFwd/Source/*") { return '$(PluginSource)' + $p.Substring("$hostPluginFwd/Source".Length) }
    if ($p -like "$hostPluginFwd/Intermediate/Build/Win64/UnrealEditor/Inc*") { return '$(PluginGenerated)' + $p.Substring("$hostPluginFwd/Intermediate/Build/Win64/UnrealEditor/Inc".Length) }
    if ($p -match '^[A-Za-z]:') { throw "Unexpected absolute include path: $p" }
    # relative to Engine/Source
    $full = [IO.Path]::GetFullPath([IO.Path]::Combine($engineSource, $p)).Replace('\', '/')
    $rootFwd = $EngineRoot.Replace('\', '/')
    return '$(SDK)' + $full.Substring($rootFwd.Length)
}

# --- 2. compiler flags + include paths ---------------------------------------------------------------------
$anyCpp = Get-ChildItem $int -Filter *.cpp.obj.rsp | Where-Object Name -notlike '*.gen.cpp.obj.rsp' | Select-Object -First 1
# Modules with the default PCH mode use a shared PCH of an engine module (e.g. SharedPCH.UnrealEd.Cpp20.h, generated
# into the host project's Intermediate). Build.ps1 uses no PCHs: its contents are folded into Definitions.h (step 4).
$sharedPch = Get-Content $anyCpp.FullName | Where-Object { $_ -match '^/FI"(.*[\\/]SharedPCH\.[^"\\/]+\.h)"$' } | ForEach-Object { $Matches[1] } | Select-Object -First 1
$pchDir = if ($sharedPch) { (Split-Path $sharedPch).Replace('\', '/') }
$shared = Get-Content "$int\$ModuleName.Shared.rsp"
$includes = [Collections.Generic.List[string]]::new()
$flags = [Collections.Generic.List[string]]::new()
foreach ($l in $shared) {
    if ($pchDir -and $l -eq "/I `"$pchDir`"") { continue }
    if ($l -match '^/I "(.*)"$') { $includes.Add((Convert-IncludePath $Matches[1])) }
    elseif ($l -match '^/external:I "(.*)"$') {
        $p = $Matches[1]
        if ($p -match '^[A-Za-z]:') { continue }  # MSVC / Windows SDK, resolved by Build.ps1
        $includes.Add('external ' + (Convert-IncludePath $p))
    }
    else { $flags.Add($l) }
}
# per-file flags (identical for all files of the module)
$perFile = Get-Content $anyCpp.FullName | Where-Object { $_ -notmatch '^("|@|/FI|/Fo|/Fp|/Yu|/experimental:log|/sourceDependencies)' }
$flags.AddRange([string[]]$perFile)
Set-Content "$configDir\IncludePaths.txt" $includes
Set-Content "$configDir\CompilerFlags.rsp" ($flags | Select-Object -Unique)

# --- 3. linker flags, libraries ------------------------------------------------------------------------------
$link = Get-Content "$int\UnrealEditor-$ModuleName.dll.rsp"
$lflags = [Collections.Generic.List[string]]::new(); $mods = [Collections.Generic.List[string]]::new(); $sys = [Collections.Generic.List[string]]::new()
$thirdParty = [Collections.Generic.List[string]]::new(); $siblings = [Collections.Generic.List[string]]::new()
foreach ($l in $link) {
    if ($l -match 'UnrealEditor-(\w+)\.lib"$') { if ($Matches[1] -in $ModuleNames) { $siblings.Add($Matches[1]) } else { $mods.Add($Matches[1]) } }
    elseif ($l -match '^"(\w+\.lib)"$') { $sys.Add($Matches[1]) }
    # vendor libraries of engine ThirdParty modules (ICU, EOSSDK, ...); UBT links them relative to Engine\Source
    elseif ($l -match '^"(ThirdParty/[^"]+\.lib)"$') { $thirdParty.Add('Engine\Source\' + $Matches[1].Replace('/', '\')) }
    elseif ($l -match '\.(obj|lib|res)"$' -or $l -match '^/(OUT|PDB|IMPLIB|LIBPATH|NATVIS|DELAYLOAD:"libsndfile)') { }
    else { $lflags.Add($l) }
}
Set-Content "$configDir\LinkerFlags.rsp" $lflags
Set-Content "$configDir\EngineModules.txt" $mods
Set-Content "$configDir\SystemLibs.txt" $sys
Set-Content "$configDir\ThirdPartyLibs.txt" $thirdParty
Set-Content "$configDir\PluginModules.txt" $siblings

# --- 4. definitions, retargeted to the devkit's ShooterGameEditor target ------------------------------------
$overrides = [ordered]@{
    'UBT_MODULE_MANIFEST'           = '"ShooterGameEditor.modules"'
    'UBT_MODULE_MANIFEST_DEBUGGAME' = '"ShooterGameEditor-Win64-DebugGame.modules"'
    'UE_APP_NAME'                   = '"ShooterGameEditor"'
    'UE_PROJECT_NAME'               = 'ShooterGame'
    'UE_TARGET_NAME'                = 'ShooterGameEditor'
}
# Plugin specific overrides (<Plugin>\Build\DefinitionOverrides.txt, lines NAME=VALUE): definitions whose value
# UBT derives from the vanilla engine but must differ for the devkit (e.g. a feature that needs a plugin the devkit
# has no binaries of).
$overrideFile = Join-Path $PluginBuildDir 'DefinitionOverrides.txt'
if (Test-Path $overrideFile) {
    foreach ($l in Get-Content $overrideFile) { if ($l -match '^\s*([A-Za-z_]\w*)\s*=\s*(.*?)\s*$') { $overrides[$Matches[1]] = $Matches[2] } }
}
function Convert-Definitions([string[]] $lines) {
    foreach ($l in $lines) {
        if ($l -match '^#define (\S+)' -and $overrides.Contains($Matches[1])) { "#define $($Matches[1]) $($overrides[$Matches[1]])" } else { $l }
    }
}
if ($sharedPch) {
    # Same order as UBT: shared definitions of the PCH module (Definitions.h), the PCH's header, then the module's
    # own definitions (Definitions.<Module>.h: #include of the shared definitions + #undef/#define of the differences).
    # The last two go to SharedPch.h, which Build.ps1 force-includes after Definitions.h and the ARK forward declarations.
    $pchLines = Get-Content $sharedPch
    $sharedDefs = $pchLines | Where-Object { $_ -match '^#include "(SharedDefinitions\.[^"]+)"' } | ForEach-Object { Join-Path (Split-Path $sharedPch) $Matches[1] }
    $defs = @(Convert-Definitions (Get-Content $sharedDefs))
    $pchDefs = @("// Shared PCH $(Split-Path -Leaf $sharedPch) of the UBT build (Build.ps1 uses no PCHs: force-included instead)", '#pragma once') +
        @($pchLines | Where-Object { $_ -match '^#include ' -and $_ -notmatch 'SharedDefinitions\.' }) +
        @(Convert-Definitions (Get-Content "$int\Definitions.$ModuleName.h" | Where-Object { $_ -ne '#pragma once' -and $_ -notmatch '^#include "SharedDefinitions\.' }))
    Set-Content "$configDir\SharedPch.h" $pchDefs
}
else {
    $defs = @(Convert-Definitions (Get-Content "$int\Definitions.h"))
    Remove-Item "$configDir\SharedPch.h" -ErrorAction SilentlyContinue
}
$defs += @(
    '',
    '// --- ARK Devkit build environment (recovered from the devkit binaries, see the README.md of the build root) ---',
    '// The devkit is built without the Low Level Memory tracker: FLowLevelMemTracker/FLLMScope are not exported.',
    '#define LLM_ENABLED_IN_CONFIG 0',
    '#define WITH_DEV_MODS 0'
)
Set-Content "$configDir\Definitions.h" $defs

# --- 5. UHT module closure ------------------------------------------------------------------------------------
$manifestPath = Join-Path $hostDir 'Intermediate\Build\Win64\UnrealEditor\UnrealEditor.uhtmanifest'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$byName = @{}; foreach ($m in $manifest.Modules) { $byName[$m.Name] = $m }
$closure = [Collections.Generic.HashSet[string]]::new(); $queue = [Collections.Generic.Queue[string]]::new(); $queue.Enqueue($ModuleName)
# A module without reflected types has no manifest entry: start from the modules whose UHT output it includes.
$hasUht = $byName.ContainsKey($ModuleName)
if (-not $hasUht) { foreach ($l in $shared) { if ($l -match '^/I "\.\./Intermediate/Build/Win64/UnrealEditor/Inc/(\w+)/UHT"$' -and $byName.ContainsKey($Matches[1])) { $queue.Enqueue($Matches[1]) } } }
while ($queue.Count) { $n = $queue.Dequeue(); if (-not $closure.Add($n)) { continue }; foreach ($d in $byName[$n].ModuleDependencies) { if ($byName.ContainsKey($d)) { $queue.Enqueue($d) } } }
foreach ($m in $ModuleNames) { [void]$closure.Remove($m) }   # the plugin's own modules are UHT'd by Invoke-Uht.ps1 <Plugin>
$rootPrefix = $EngineRoot.TrimEnd('\') + '\'
function Rel([string] $p) { if ($p.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { return '$(SDK)\' + $p.Substring($rootPrefix.Length) } else { throw "path outside engine: $p" } }
$reflected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$uhtModules = foreach ($m in ($manifest.Modules | Where-Object { $closure.Contains($_.Name) })) {
    $copy = $m | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $copy.BaseDirectory = Rel $m.BaseDirectory
    $copy.IncludePaths = @($m.IncludePaths | ForEach-Object { Rel $_ })
    $copy.OutputDirectory = Rel $m.OutputDirectory
    $copy.GeneratedCPPFilenameBase = Rel $m.GeneratedCPPFilenameBase
    # Private headers are kept: public UPROPERTYs may reference types UHT only finds in private headers.
    foreach ($k in 'ClassesHeaders', 'PublicHeaders', 'InternalHeaders', 'PrivateHeaders') {
        $copy.$k = @($m.$k | ForEach-Object { [void]$reflected.Add($_); Rel $_ })
    }
    $copy
}
ConvertTo-Json -InputObject @($uhtModules) -Depth 8 | Set-Content "$configDir\UhtModules.json"
# UhtPluginModule.json only exists for modules with reflected types; Build.ps1 skips the plugin UHT run without it
if ($hasUht) {
    $baModule = $byName[$ModuleName] | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $baModule | Select-Object * -ExcludeProperty BaseDirectory, IncludePaths, OutputDirectory, ClassesHeaders, PublicHeaders, InternalHeaders, PrivateHeaders, GeneratedCPPFilenameBase |
        ConvertTo-Json -Depth 8 | Set-Content "$configDir\UhtPluginModule.json"
}
else { Remove-Item "$configDir\UhtPluginModule.json" -ErrorAction SilentlyContinue }
$manifest | Select-Object IsGameTarget, TargetSettings | ConvertTo-Json -Depth 8 | Set-Content "$configDir\UhtTarget.json"

# --- 6. header set for the SDK: everything the compile included + all reflected headers of the closure -------
# MSVC dependency files contain lower-cased paths; restore the on-disk casing.
$dirCache = @{}
function Get-TrueCase([string] $path) {
    $parts = $path.Split('\')
    $cur = $parts[0].ToUpper() + '\'
    for ($i = 1; $i -lt $parts.Count; $i++) {
        if (-not $dirCache.ContainsKey($cur)) {
            $map = @{}; foreach ($e in [IO.Directory]::GetFileSystemEntries($cur)) { $map[[IO.Path]::GetFileName($e).ToLowerInvariant()] = [IO.Path]::GetFileName($e) }
            $dirCache[$cur] = $map
        }
        $name = $dirCache[$cur][$parts[$i].ToLowerInvariant()]
        if (-not $name) { throw "Not found: $path" }
        $cur = [IO.Path]::Combine($cur, $name)
    }
    return $cur
}
$rootPrefix = (Get-TrueCase $EngineRoot.TrimEnd('\')) + '\'
$headers = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$depFiles = @(Get-ChildItem $int -Filter *.dep.json)
if ($sharedPch) { $depFiles += Get-Item "$sharedPch.dep.json" }
foreach ($f in $depFiles) {
    foreach ($i in (Get-Content $f.FullName -Raw | ConvertFrom-Json).Data.Includes) {
        if (-not $i.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($i -match '\\Intermediate\\Build\\') { continue }   # generated headers are regenerated by UHT
        [void]$headers.Add((Get-TrueCase $i).Substring($rootPrefix.Length))
    }
}
foreach ($r in $reflected) { [void]$headers.Add((Get-TrueCase $r).Substring($rootPrefix.Length)) }
foreach ($t in $thirdParty) { [void]$headers.Add($t) }   # copied into the SDK like the headers
Set-Content "$configDir\SdkHeaders.txt" ($headers | Sort-Object)
Write-Host "Captured: $($includes.Count) include paths, $($mods.Count) engine modules, $($closure.Count) UHT modules, $($headers.Count) SDK headers"
}
