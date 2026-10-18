<#
.SYNOPSIS
  Proves that the plugin's machine code does not depend on the layout or vtables of the types listed in
  Config\AbiExclusions.txt (the ARK types whose ABI changes are not reproduced in the SDK).

.DESCRIPTION
  1. Copies the SDK to SDP\ (root) and injects into every excluded class/struct a 64 byte data member and, for
     polymorphic types, a virtual function, at the very start of the type (same source line, so no __LINE__ or
     UHT line number changes). This shifts every data member offset and every vtable slot.
  2. Compiles the plugin against the probe SDK (intermediates in <Plugin>\Build\Probe_Interm). Both paths have
     the same length as SDK\ / <Plugin>\Build\Intermediate, so embedded source paths keep their sizes.
  3. Disassembles every object file of the normal build and of the probe build and compares code and section
     sizes (symbol names that encode source paths - string literals, ensure() state - are normalized).
  Identical code means no instruction of the plugin depends on those layouts. Run after a successful build.
.EXAMPLE
  .\Tools\Test-LayoutIndependence.ps1 BlueprintAssist
#>
param(
    [Parameter(Position = 0)] [string] $Plugin,   # plugin folder name below Plugins
    [switch] $CompareOnly,   # skip probe SDK creation and the probe build (reuse <Plugin>\Build\Probe_Interm)
    [switch] $Keep           # keep SDP\ and <Plugin>\Build\Probe_Interm after a successful run
)
. "$PSScriptRoot\..\Common.ps1"
Initialize-Plugin $Plugin
$msvc = Find-Msvc
$dumpbin = Join-Path $msvc.Bin 'dumpbin.exe'
$tool = Initialize-PdbLayout
$refPdb = Join-Path $IntDir "Bin\$ModulePrefix$ModuleName.pdb"
if (-not (Test-Path $refPdb)) { throw "Build the plugin first ($refPdb missing)" }

# Probe paths have the same length as the normal ones, so embedded source paths and string literals keep their sizes:
# the SDK folder name with its last character replaced (SDK -> SDP), <Plugin>\Build\Probe_Interm for Intermediate.
$sdkLeaf  = Split-Path -Leaf $SdkRoot
$probeSdk = Join-Path (Split-Path $SdkRoot) ($sdkLeaf.Substring(0, $sdkLeaf.Length - 1) + $(if ($sdkLeaf.EndsWith('P')) { 'Q' } else { 'P' }))
$probeInt = Join-Path $PluginBuildDir 'Probe_Interm'
if ($IntDir -ne (Join-Path $PluginBuildDir 'Intermediate')) { throw 'Test-LayoutIndependence needs the default intermediate folder (unset ARK_PLUGIN_INT_ROOT)' }
$sdkPattern = '(' + [regex]::Escape($SdkRoot) + '|' + [regex]::Escape($probeSdk) + ')\\'
if (-not $CompareOnly) {
Write-Step "Creating probe SDK in $probeSdk"
& robocopy $SdkRoot $probeSdk /MIR /NFL /NDL /NJH /NJS /NP /XD Lib | Out-Null
New-Item -ItemType Directory -Force (Join-Path $probeSdk 'Lib') | Out-Null
& $tool probe $refPdb $probeSdk $SdkRoot (Join-Path $SharedConfigDir 'AbiExclusions.txt')
if ($LASTEXITCODE -ne 0) { throw 'probe injection failed' }

Write-Step 'Compiling the plugin against the probe SDK'
$env:ARK_SDK_ROOT = $probeSdk; $env:ARK_PLUGIN_INT_ROOT = $probeInt
try {
    if (Test-Path "$probeInt\Obj") { Remove-Item -Recurse -Force "$probeInt\Obj" }
    & "$BuildDir\Build.ps1" $PluginRoot -CompileOnly
} finally { Remove-Item Env:ARK_SDK_ROOT, Env:ARK_PLUGIN_INT_ROOT }
}

Write-Step 'Comparing generated code'
function Get-Code([string] $obj) {
    (& $dumpbin /nologo /disasm $obj) |
        Where-Object { $_ -notmatch '^Dump of file|^File Type|^\s+[0-9A-F]+ \.debug\$' } |   # debug info sizes legitimately differ
        ForEach-Object {
            # names that encode source paths: string literals and ensure() state keyed by a hash of __FILE__
            $_ -replace '\?\?_C@_[^\s\],]+', '<string>' -replace '(bGEnsureHasExecuted@\$0)\??[A-P]+', '$1<hash>' -replace $sdkPattern, '<sdk>\' }
}
function Get-Blocks([string[]] $lines) {
    $blocks = [Collections.Generic.List[string]]::new(); $cur = [Text.StringBuilder]::new()
    foreach ($l in $lines) {
        if ($l.Trim() -eq '') { if ($cur.Length) { $blocks.Add($cur.ToString()); [void]$cur.Clear() } }
        else { [void]$cur.AppendLine($l) }
    }
    if ($cur.Length) { $blocks.Add($cur.ToString()) }
    return ,$blocks.ToArray()
}
$diffs = 0; $n = 0
foreach ($obj in Get-ChildItem (Join-Path $IntDir 'Obj') -Filter *.obj) {
    $other = Join-Path "$probeInt\Obj" $obj.Name
    $n++
    if (-not (Test-Path $other)) { Write-Host "  missing in probe build: $($obj.Name)" -ForegroundColor Red; $diffs++; continue }
    # compare the multiset of code blocks (one block per COMDAT function); COMDAT order may legitimately differ
    $a = Get-Blocks (Get-Code $obj.FullName); $b = Get-Blocks (Get-Code $other)
    $d = Compare-Object $a $b
    if ($d) {
        $diffs++
        Write-Host "  DIFFERENT: $($obj.Name)" -ForegroundColor Red
        $d | Select-Object -First 10 | ForEach-Object { Write-Host "    $($_.SideIndicator) $($_.InputObject)" }
    }
}
if ($diffs -eq 0) {
    Write-Host "OK: all $n object files have identical code - the plugin does not depend on the excluded types' layouts" -ForegroundColor Green
    if (-not $Keep) { Remove-Item -Recurse -Force $probeSdk, $probeInt }
}
else { throw "$diffs of $n object files differ" }
