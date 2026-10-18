<#
.SYNOPSIS
  Generates x64 import libraries (.lib) for ShooterGameEditor-<Module>.dll files of the ARK Devkit.

.DESCRIPTION
  The devkit ships DLLs + PDBs but no import libraries. For every requested module this script reads
  the export table with dumpbin, classifies every export as code or data (by the section its RVA
  falls into), writes a .def file and turns it into an import library with lib.exe.
  The devkit itself is only read, never modified.
#>
param(
    [Parameter(Mandatory)] [string]   $DevkitBinaries,   # ...\ARKDevkitBeta\Engine\Binaries\Win64
    [Parameter(Mandatory)] [string[]] $Modules,          # module names, e.g. Core, UnrealEd
    [Parameter(Mandatory)] [string]   $OutDir,
    [Parameter(Mandatory)] [string]   $MsvcBin,          # ...\VC\Tools\MSVC\<ver>\bin\Hostx64\x64
    [string] $Prefix = 'ShooterGameEditor-',
    [switch] $Force
)
$ErrorActionPreference = 'Stop'
$dumpbin = Join-Path $MsvcBin 'dumpbin.exe'
$libexe  = Join-Path $MsvcBin 'lib.exe'
New-Item -ItemType Directory -Force $OutDir | Out-Null

foreach ($module in $Modules) {
    $dllName = "$Prefix$module.dll"
    $dll     = Join-Path $DevkitBinaries $dllName
    if (-not (Test-Path $dll)) {
        # Plugin modules live next to their plugin, search the whole Engine tree once.
        $found = Get-ChildItem -Path (Split-Path (Split-Path (Split-Path $DevkitBinaries))) -Recurse -Filter $dllName -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $found) { throw "Devkit module not found: $dllName" }
        $dll = $found.FullName
    }
    $lib = Join-Path $OutDir "$Prefix$module.lib"
    if (-not $Force -and (Test-Path $lib) -and ((Get-Item $lib).LastWriteTimeUtc -gt (Get-Item $dll).LastWriteTimeUtc)) { continue }

    # Section table -> list of (start, end, isCode)
    $sections = @()
    $current = $null
    foreach ($line in (& $dumpbin /nologo /headers $dll)) {
        if ($line -match '^SECTION HEADER #\d+') { $current = @{} ; continue }
        if ($null -eq $current) { continue }
        if ($line -match '^\s+([0-9A-F]+) virtual size') { $current.Size = [Convert]::ToUInt32($Matches[1], 16) }
        elseif ($line -match '^\s+([0-9A-F]+) virtual address') { $current.Va = [Convert]::ToUInt32($Matches[1], 16) }
        elseif ($line -match '^\s+[0-9A-F]+ flags') { $current.Flags = $true }
        elseif ($current.Flags -and $line -match '^\s+Execute') {
            $current.Code = $true
        }
        elseif ($current.Flags -and $line.Trim() -eq '') {
            if ($current.ContainsKey('Va')) { $sections += [pscustomobject]@{ Start = $current.Va; End = $current.Va + $current.Size; Code = [bool]$current.Code } }
            $current = $null
        }
    }

    $def = [System.Text.StringBuilder]::new()
    [void]$def.AppendLine("LIBRARY `"$dllName`"")
    [void]$def.AppendLine('EXPORTS')
    $count = 0
    $inTable = $false
    foreach ($line in (& $dumpbin /nologo /exports $dll)) {
        if ($line -match '^\s+ordinal\s+hint\s+RVA\s+name') { $inTable = $true; continue }
        if (-not $inTable) { continue }
        if ($line -match '^\s+Summary') { break }
        # "  ordinal hint RVA      name" ; forwarded exports have no RVA and are skipped
        if ($line -match '^\s+\d+\s+[0-9A-F]+\s+([0-9A-F]{8})\s+(\S+)') {
            $rva  = [Convert]::ToUInt32($Matches[1], 16)
            $name = $Matches[2]
            $sec  = $sections | Where-Object { $rva -ge $_.Start -and $rva -lt $_.End } | Select-Object -First 1
            if ($sec -and -not $sec.Code) { [void]$def.AppendLine("    $name DATA") } else { [void]$def.AppendLine("    $name") }
            $count++
        }
    }
    $defPath = Join-Path $OutDir "$Prefix$module.def"
    [System.IO.File]::WriteAllText($defPath, $def.ToString())
    & $libexe /nologo /machine:x64 "/def:$defPath" "/out:$lib" | Where-Object { $_ -notmatch 'Creating library' }
    if ($LASTEXITCODE -ne 0) { throw "lib.exe failed for $module" }
    Write-Host ("  {0,-28} {1,6} exports" -f $module, $count)
}
