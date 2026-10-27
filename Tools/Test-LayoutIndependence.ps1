<#
.SYNOPSIS
  Proves that the plugin's machine code does not depend on the layout or vtables of the types listed in
  Config\AbiExclusions.txt (the ARK types whose ABI changes are not reproduced in the SDK).

.DESCRIPTION
  1. Copies the SDK to SDP\ (root) and injects into every excluded class/struct a 64 byte data member where its
     layout starts to differ from the devkit's (before the first differing base or member, before the closing brace
     when the devkit only appends members) and, for polymorphic types whose introduced virtuals differ, a virtual
     function at the start of the type (same source line, so no __LINE__ or UHT line number changes). This shifts
     every data member offset from the first difference on and every vtable slot. Exclusions marked "[no probe]"
     (per-DLL private copies) are skipped.
  2. Compiles the plugin against the probe SDK (intermediates in <Plugin>\Build\Probe_Interm). Both paths have
     the same length as SDK\ / <Plugin>\Build\Intermediate, so embedded source paths keep their sizes.
  3. Disassembles every object file of the normal build and of the probe build and compares code and section
     sizes (symbol names that encode source paths - string literals, ensure() state, anonymous namespaces, UHT
     file IDs and package hashes - are normalized; .bss is not compared: static variables are ordered by their
     path-dependent names, so its padding differs).
  Identical code means no instruction of the plugin depends on those layouts. Run after a successful build.
.EXAMPLE
  .\Tools\Test-LayoutIndependence.ps1 BlueprintAssist
#>
param(
    [Parameter(Position = 0)] [string] $Plugin,   # plugin folder name below Plugins
    [switch] $CompareOnly,   # skip probe SDK creation and the probe build (reuse <Plugin>\Build\Probe_Interm)
    [switch] $Keep,          # keep SDP\ and <Plugin>\Build\Probe_Interm after a successful run
    [int]    $Jobs = [Environment]::ProcessorCount   # parallel compiler processes of the probe build
)
. "$PSScriptRoot\..\Common.ps1"
Initialize-Plugin $Plugin
$msvc = Find-Msvc
$dumpbin = Join-Path $msvc.Bin 'dumpbin.exe'
$tool = Initialize-PdbLayout
$refPdbs = @($ModuleNames | ForEach-Object { Join-Path $IntDir "Bin\$ModulePrefix$_.pdb" })
foreach ($p in $refPdbs) { if (-not (Test-Path $p)) { throw "Build the plugin first ($p missing)" } }

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
& $tool probe ($refPdbs -join ';') $probeSdk $SdkRoot (Join-Path $SharedConfigDir 'AbiExclusions.txt') @(Get-DevkitPdbs) --renames (Join-Path $SharedConfigDir 'AbiRenames.txt')
if ($LASTEXITCODE -ne 0) { throw 'probe injection failed' }

Write-Step 'Compiling the plugin against the probe SDK'
$env:ARK_SDK_ROOT = $probeSdk; $env:ARK_PLUGIN_INT_ROOT = $probeInt
try {
    if (Test-Path "$probeInt\Obj") { Remove-Item -Recurse -Force "$probeInt\Obj" }
    & "$BuildDir\Build.ps1" $PluginRoot -CompileOnly -Jobs $Jobs
} finally { Remove-Item Env:ARK_SDK_ROOT, Env:ARK_PLUGIN_INT_ROOT }
}

Write-Step 'Comparing generated code'
# UHT package hashes (CONSTRUCT_RELOAD_VERSION_INFO in *.init.gen.cpp) are computed over generated code that
# contains the source paths
$pkgHashes = @(Get-ChildItem "$IntDir\Inc", "$probeInt\Inc" -Recurse -Filter *.init.gen.cpp -ErrorAction SilentlyContinue |
    Select-String 'CONSTRUCT_RELOAD_VERSION_INFO\(FPackageReloadVersionInfo, 0x([0-9A-Fa-f]+)' | ForEach-Object { $_.Matches[0].Groups[1].Value })
$pkgPattern = if ($pkgHashes) { '\b0?(' + ($pkgHashes -join '|') + ')h\b' } else { '(?!)' }
function Get-Code([string] $obj) {
    (& $dumpbin /nologo /disasm $obj) |
        # debug info sizes legitimately differ; instruction bytes are dropped (the disassembly states every operand,
        # and the bytes would carry the normalized values below)
        Where-Object { $_ -notmatch '^Dump of file|^File Type|^\s+[0-9A-F]+ \.debug\$|^\s+[0-9A-F]+ \.bss$|^\s+([0-9A-F]{2} ?)+$' } |
        ForEach-Object { $_ -replace '^(\s+[0-9A-F]{16}: )([0-9A-F]{2} )+\s*', '$1' } |
        ForEach-Object {
            # names that encode source paths: string literals, ensure() state keyed by a hash of __FILE__ and
            # anonymous namespaces (?A0x<hash>, derived from the compilation's paths) and UHT file IDs of patched
            # plugin sources (Z_CompiledInDeferFile_FID_..._Build_Intermediate_Source_...)
            $_ -replace '\?\?_C@_[^\s\],]+', '<string>' -replace '(bGEnsureHasExecuted@\$0)\??[A-P]+', '$1<hash>' -replace '\?A0x[0-9a-f]+@', '?A0x<hash>@' -replace '_Build_(Intermediate|Probe_Interm)_Source_', '_Build_<int>_Source_' -replace $pkgPattern, '<package hash>' -replace $sdkPattern, '<sdk>\' }
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
$objRoot = Join-Path $IntDir 'Obj'
foreach ($obj in Get-ChildItem $objRoot -Recurse -Filter *.obj) {   # Obj\<Module>\ in multi-module plugins
    $rel = $obj.FullName.Substring($objRoot.Length + 1)
    $other = Join-Path "$probeInt\Obj" $rel
    $n++
    if (-not (Test-Path $other)) { Write-Host "  missing in probe build: $rel" -ForegroundColor Red; $diffs++; continue }
    # compare the multiset of code blocks (one block per COMDAT function); COMDAT order may legitimately differ
    $a = Get-Blocks (Get-Code $obj.FullName); $b = Get-Blocks (Get-Code $other)
    $d = Compare-Object $a $b
    if ($d) {
        $diffs++
        Write-Host "  DIFFERENT: $rel" -ForegroundColor Red
        $d | Select-Object -First 10 | ForEach-Object { Write-Host "    $($_.SideIndicator) $($_.InputObject)" }
    }
}
if ($diffs -eq 0) {
    Write-Host "OK: all $n object files have identical code - the plugin does not depend on the excluded types' layouts" -ForegroundColor Green
    if (-not $Keep) { Remove-Item -Recurse -Force $probeSdk, $probeInt }
}
else { throw "$diffs of $n object files differ" }
