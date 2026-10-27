<#
.SYNOPSIS
  Re-creates the ABI patches of the shared SDK headers from scratch, from the baseline reference PDBs.

.DESCRIPTION
  1. Resets the SDK headers (New-ArkSdk.ps1 -Clean -HeadersOnly: vanilla headers + ArkPatches\ + ARK classes).
  2. Runs PdbLayout patch once with all reference PDBs in Intermediate\BaselinePdb (plugin PDBs built against the
     reset SDK, see -SaveBaseline): every type is patched from the first reference PDB that has it, against the devkit
     PDBs of every module any plugin links.
  3. Declares the ARK-only types the patches use (Update-ArkTypes) and applies Tools\Apply-ManualArkEdits.sh.
  Afterwards run New-ArkSdk.ps1 (UHT + import libraries), build and audit the plugins, and store the result with
  Tools\Export-ArkPatches.ps1 once the audits are clean.
.EXAMPLE
  .\Tools\Repatch-ArkSdk.ps1
  .\Tools\Repatch-ArkSdk.ps1 -SaveBaseline GitSourceControl, ue_mcp_bridge   # after building them against the reset SDK
#>
param(
    [string[]] $SaveBaseline   # copy the current PDBs of these plugins to Intermediate\BaselinePdb (no patching)
)
. "$PSScriptRoot\..\Common.ps1"
$baselineDir = Join-Path $SharedIntDir 'BaselinePdb'
# the baseline PDBs match the reset SDK only as long as ArkPatches\ is unchanged: after Export-ArkPatches.ps1 the
# patches already contain what this script would add a second time
$patchStampFile = Join-Path $baselineDir 'ArkPatches.stamp'
$patchStamp = (Get-ChildItem (Join-Path $BuildDir 'ArkPatches') -Filter *.patch | Sort-Object Name |
    ForEach-Object { "$($_.Name):$((Get-FileHash $_.FullName -Algorithm SHA1).Hash)" }) -join "`n"

if ($SaveBaseline) {
    New-Item -ItemType Directory -Force $baselineDir | Out-Null
    Set-Content $patchStampFile $patchStamp
    foreach ($p in $SaveBaseline) {
        Initialize-Plugin $p
        foreach ($m in $ModuleNames) {
            $pdb = Join-Path $IntDir "Bin\$ModulePrefix$m.pdb"
            if (-not (Test-Path $pdb)) { throw "$pdb missing: build $p against the reset SDK first (Build.ps1 $p -ForceLink)" }
            Copy-Item $pdb $baselineDir -Force
            Write-Host "  baseline: $(Split-Path -Leaf $pdb)"
        }
    }
    return
}

$refs = @(Get-ChildItem $baselineDir -Filter *.pdb -ErrorAction SilentlyContinue | ForEach-Object FullName)
if (-not $refs) { throw "No baseline PDBs in $baselineDir (see -SaveBaseline)" }
if (-not (Test-Path $patchStampFile)) {
    Write-Warning "No ArkPatches stamp for the baseline PDBs: make sure ArkPatches\ is unchanged since they were built"
}
elseif ((Get-Content $patchStampFile -Raw).Trim() -ne $patchStamp.Trim()) {
    throw "ArkPatches\ changed since the baseline PDBs were saved (Export-ArkPatches.ps1?): restore it, or rebuild the baseline plugins against the reset SDK and save them again (-SaveBaseline)"
}

Write-Step 'Resetting the SDK headers'
& "$BuildDir\New-ArkSdk.ps1" -Clean -HeadersOnly | Out-Null

Write-Step "Patching from $($refs.Count) reference PDB(s)"
$tool = Initialize-PdbLayout
$order = 'Core', 'CoreUObject', 'ApplicationCore', 'InputCore', 'Json', 'SlateCore', 'Slate', 'RenderCore', 'Engine'
$mods = $order + @(Get-AllPluginConfigLines 'EngineModules.txt' | Where-Object { $_ -notin $order })
$arkPdbs = $mods | ForEach-Object { Join-Path $DevkitBinaries "$ModulePrefix$_.pdb" } | Where-Object { Test-Path $_ }
& $tool patch ($refs -join ';') $SdkRoot $SdkRoot (Join-Path $SharedIntDir 'patch-report.txt') @arkPdbs `
    --exclude (Join-Path $SharedConfigDir 'AbiExclusions.txt') --ark-classes (Join-Path $SharedConfigDir 'ArkClasses.txt') `
    --renames (Join-Path $SharedConfigDir 'AbiRenames.txt')
if ($LASTEXITCODE -ne 0) { throw 'PdbLayout patch failed' }
Update-ArkTypes
Invoke-Bash 'Apply-ManualArkEdits.sh' @()
Write-Step 'Done: run New-ArkSdk.ps1, then build and audit the plugins'
