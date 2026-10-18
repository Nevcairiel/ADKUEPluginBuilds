<#
.SYNOPSIS
  Verifies the ABI of a built plugin against the ARK Devkit.

.DESCRIPTION
  Compares every class/struct/union/enum recorded in the plugin's PDB (i.e. every type the plugin's code was
  compiled against) with the same type in the devkit's ShooterGameEditor-*.pdb files and reports differences in
  data layout, vtable layout (introduced virtual functions), enumerator values and method access.
  Writes <Plugin>\Build\Intermediate\abi-audit.txt (all differences) and abi-relevant.txt (ABI relevant only).
  With -Patch the differences are applied to the shared SDK headers instead (see Tools\PdbLayout\Patcher.cs).
.EXAMPLE
  .\Tools\Test-ArkAbi.ps1 BlueprintAssist
#>
param(
    [Parameter(Position = 0)] [string] $Plugin,   # plugin folder name below Plugins
    [switch] $Patch
)
. "$PSScriptRoot\..\Common.ps1"
Initialize-Plugin $Plugin

$tool = Initialize-PdbLayout
$pdb = Join-Path $IntDir "Bin\$ModulePrefix$ModuleName.pdb"
if (-not (Test-Path $pdb)) { throw "Build the plugin first ($pdb missing)" }

# lower modules first: a type is taken from the first devkit PDB that defines it
$order = 'Core', 'CoreUObject', 'ApplicationCore', 'InputCore', 'Json', 'SlateCore', 'Slate', 'RenderCore', 'Engine'
$mods = $order + (Get-Content "$ConfigDir\EngineModules.txt" | Where-Object { $_ -notin $order })
$arkPdbs = $mods | ForEach-Object { Join-Path $DevkitBinaries "$ModulePrefix$_.pdb" } | Where-Object { Test-Path $_ }   # modules missing in the devkit

$exclusions = Join-Path $SharedConfigDir 'AbiExclusions.txt'
if ($Patch) {
    & $tool patch $pdb $SdkRoot $SdkRoot (Join-Path $IntDir 'patch-report.txt') @arkPdbs --exclude $exclusions
} else {
    & $tool audit $pdb (Join-Path $IntDir 'abi-audit.txt') @arkPdbs
    $relevant = Invoke-Bash 'filter-audit.sh' ((Join-Path $IntDir 'abi-audit.txt').Replace('\', '/'))
    Set-Content (Join-Path $IntDir 'abi-relevant.txt') $relevant
    $excluded = Get-Content $exclusions | ForEach-Object { $_.Split('#')[0].Trim() } | Where-Object { $_ }
    $types = $relevant | Where-Object { $_ -match '^\[[^\]]*\] \w+ (.*): sizeof' } | ForEach-Object { $Matches[1] }
    $open = @($types | Where-Object { $_ -notin $excluded })
    $known = @($types | Where-Object { $_ -in $excluded })
    Write-Host "ABI audit: $($open.Count) types with unexplained ABI differences, $($known.Count) excluded types (see Config\AbiExclusions.txt)"
    $open | ForEach-Object { Write-Host "  $_" }
}
