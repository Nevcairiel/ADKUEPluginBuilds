<#
.SYNOPSIS
  Runs UnrealHeaderTool (hosted by the vanilla UnrealBuildTool) on the patched SDK headers and the module of
  one plugin, or with -EngineOnly on the engine modules needed by all plugins. UHT only rewrites outputs whose
  content changed.
.EXAMPLE
  .\Invoke-Uht.ps1 BlueprintAssist
  .\Invoke-Uht.ps1 -EngineOnly
#>
param(
    [Parameter(Position = 0)] [string] $Plugin,
    [switch] $EngineOnly
)
. "$PSScriptRoot\Common.ps1"

function Expand($v) { if ($v -is [string]) { $v.Replace('$(SDK)', $SdkRoot) } else { $v } }

function Read-UhtModules([string] $file) {
    foreach ($m in (Get-Content $file -Raw | ConvertFrom-Json)) {
        foreach ($k in 'BaseDirectory', 'OutputDirectory', 'GeneratedCPPFilenameBase') { $m.$k = Expand $m.$k }
        foreach ($k in 'IncludePaths', 'ClassesHeaders', 'PublicHeaders', 'InternalHeaders', 'PrivateHeaders') { $m.$k = @($m.$k | ForEach-Object { Expand $_ }) }
        # The baseline came from an installed engine where engine outputs are prebuilt; here they must be generated
        # from the ARK-patched headers.
        $m.SaveExportedHeaders = $true
        $m
    }
}

$modules = [Collections.Generic.List[object]]::new()
$seen = @{}
if ($EngineOnly) {
    # engine modules of all modules of all plugins (deduplicated by name)
    $uhtDir = Join-Path $SharedIntDir 'Uht'
    $targetFile = $null
    foreach ($cfg in Get-AllModuleConfigDirs) {
        if (-not (Test-Path "$cfg\UhtModules.json")) { continue }
        $targetFile = "$cfg\UhtTarget.json"
        foreach ($m in Read-UhtModules "$cfg\UhtModules.json") { if (-not $seen.ContainsKey($m.Name)) { $seen[$m.Name] = $true; $modules.Add($m) } }
    }
    if (-not $targetFile) { throw 'No plugin has a captured build environment yet: run Tools\Capture-UbtBaseline.ps1 <Plugin> first' }
} else {
    Initialize-Plugin $Plugin
    $uhtDir = Join-Path $IntDir 'Uht'
    $targetFile = "$ConfigDir\UhtTarget.json"
    foreach ($module in $ModuleNames) {
        $cfg = Get-ModuleConfigDir $module
        foreach ($m in Read-UhtModules "$cfg\UhtModules.json") { if (-not $seen.ContainsKey($m.Name)) { $seen[$m.Name] = $true; $modules.Add($m) } }
    }
    # the plugin's own modules with reflected types, in link order (a module after the modules it depends on)
    foreach ($module in $ModuleNames) {
    Select-PluginModule $module
    if (-not (Test-Path "$ConfigDir\UhtPluginModule.json")) { continue }
    $src = Join-Path $PluginSourceRoot $ModuleName
    $reflected = Get-ChildItem $src -Recurse -Filter *.h |
        Select-String -Pattern '^\s*(UCLASS|USTRUCT|UENUM|UINTERFACE|UDELEGATE)\s*\(|DECLARE_DYNAMIC_' -List |
        ForEach-Object { $_.Path }
    $pm = Get-Content "$ConfigDir\UhtPluginModule.json" -Raw | ConvertFrom-Json
    $out = Join-Path $IntDir "Inc\$ModuleName\UHT"
    $pm | Add-Member -Force BaseDirectory $src
    $pm | Add-Member -Force IncludePaths @($PluginSourceRoot, "$src\Public", "$src\Private")
    $pm | Add-Member -Force OutputDirectory $out
    $pm | Add-Member -Force GeneratedCPPFilenameBase (Join-Path $out "$ModuleName.gen")
    $pm | Add-Member -Force ClassesHeaders @()
    $pm | Add-Member -Force PublicHeaders @($reflected | Where-Object { $_ -like "$src\Public\*" -or $_ -like "$src\Classes\*" })
    $pm | Add-Member -Force InternalHeaders @()
    $pm | Add-Member -Force PrivateHeaders @($reflected | Where-Object { -not ($_ -like "$src\Public\*" -or $_ -like "$src\Classes\*") })
    $modules.Add($pm)
    }
}
# ARK-only classes (Config\ArkClasses.txt) are part of their module: the patched engine headers derive from them
foreach ($spec in Get-ArkClasses) {
    $m = $modules | Where-Object Name -eq $spec.Module
    if (-not $m) { continue }
    $h = Join-Path $SdkRoot $spec.Header
    if ($m.PublicHeaders -notcontains $h) { $m.PublicHeaders = @($m.PublicHeaders) + $h }
}
New-Item -ItemType Directory -Force $uhtDir | Out-Null

$target = Get-Content $targetFile -Raw | ConvertFrom-Json
$manifest = [ordered]@{
    IsGameTarget             = $target.IsGameTarget
    RootLocalPath            = $SdkRoot
    TargetName               = 'ShooterGameEditor'
    ExternalDependenciesFile = Join-Path $uhtDir 'ShooterGameEditor.deps'
    TargetSettings           = $target.TargetSettings
    Modules                  = $modules
}
$manifestPath = Join-Path $uhtDir 'ShooterGameEditor.uhtmanifest'
$manifest | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $manifestPath

# UHT reads project specific config from the project directory; an empty host project is sufficient.
$project = Join-Path $uhtDir 'Host.uproject'
if (-not (Test-Path $project)) { Set-Content $project '{ "FileVersion": 3 }' }

$log = Join-Path $uhtDir 'UnrealHeaderTool.log'
& $UbtExe -Mode=UnrealHeaderTool $project $manifestPath *> $log
$exit = $LASTEXITCODE
Get-Content $log | Where-Object { $_ -match ': (error|warning)|Total of \d+ written' } | Select-Object -First 50 | Out-Host
if ($exit -ne 0) { throw "UnrealHeaderTool failed (exit $exit), see $log" }
