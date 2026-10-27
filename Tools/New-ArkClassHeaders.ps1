<#
.SYNOPSIS
  Generates the headers of ARK-only engine classes (Config\ArkClasses.txt) from the devkit PDBs into ArkHeaders\.

.DESCRIPTION
  ARK inserted new base classes into the engine's class hierarchy (AActor derives from UPrimalActor,
  UActorComponent from UPrimalActorComponent, ...). They have no source; PdbLayout genclass declares them with the
  devkit's bases, opaque data of the devkit's size and all virtual functions in vtable order. The output is checked
  in (ArkHeaders\, paths relative to the engine root) and copied into the SDK by New-ArkSdk.ps1; run this script
  again after a devkit update. Test-ArkAbi.ps1 audits the generated classes like every other type.
.EXAMPLE
  .\Tools\New-ArkClassHeaders.ps1
#>
param([string[]] $Only)   # header file names (without .h) to regenerate; default all
. "$PSScriptRoot\..\Common.ps1"
$tool = Initialize-PdbLayout
$outRoot = Join-Path $BuildDir 'ArkHeaders'
# headers of the SDK must exist to know which signature types are already defined there
if (-not (Test-Path (Join-Path $SdkRoot 'Engine'))) { throw 'Create the SDK first (New-ArkSdk.ps1)' }
foreach ($spec in Get-ArkClasses) {
    if ($Only -and [IO.Path]::GetFileNameWithoutExtension($spec.Header) -notin $Only) { continue }
    $out = Join-Path $outRoot $spec.Header
    New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
    $pdb = Join-Path $DevkitBinaries "$ModulePrefix$($spec.Module).pdb"
    & $tool genclass $pdb $out $SdkRoot "$($spec.Module.ToUpper())_API" ($spec.Includes -join ';') @($spec.Types)
    if ($LASTEXITCODE -ne 0) { throw "genclass failed for $($spec.Header)" }
}
