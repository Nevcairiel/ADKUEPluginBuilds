<#
.SYNOPSIS
  Compares our build of a plugin with the build ARK ships in the devkit (EXTENDING.md, 4.4). Read only.

.DESCRIPTION
  Needs the same plugin version on both sides and the devkit's compiler (Common.ps1). For every module with a shipped
  DLL and PDB it reports:
    Layouts      PdbLayout audit of our PDB against the shipped PDB (only excluded types may differ)
    Imports      imports and exports only one DLL has
    Commands     compile commands recorded in the PDBs (LF_BUILDINFO), file by file
    Sources      SHA-256 of every source file with code against our SDK and plugin files, with the line shifts of the
                 differing files taken from the line tables (out-of-line code only)
    Functions    instructions per function, after normalizing what depends on the build: paths in string literals,
                 ensure() state, anonymous namespaces, absolute addresses and jump tables. A second pass also ignores
                 small constants passed in registers and profiler scope names, which carry source line numbers.
  Functions with the same name in several copies are not compared. Summary on screen, details in the report folder:
  functions-real.txt (functions with real differences), bodies-ours.txt / bodies-shipped.txt (their instructions).
.EXAMPLE
  .\Tools\Compare-ShippedPlugin.ps1 BlueprintAssist
#>
param(
    [Parameter(Position = 0)] [string] $Plugin,   # plugin folder name below Plugins
    [string] $ReportDir                           # default: <Plugin>\Build\Intermediate\ShippedComparison
)
. "$PSScriptRoot\..\Common.ps1"
Initialize-Plugin $Plugin
if (-not $ReportDir) { $ReportDir = Join-Path $IntDir 'ShippedComparison' }
$msvc = Find-Msvc
$dumpbin = Join-Path $msvc.Bin 'dumpbin.exe'
$pdbutil = Find-LlvmPdbUtil
$tool = Initialize-PdbLayout
$addressRe = [regex]'\b[0-9A-F]{16}\b'
$patchNames = @{}; Get-ChildItem (Join-Path $BuildDir 'ArkPatches') -Filter *.patch | ForEach-Object { $patchNames[$_.Name.ToLowerInvariant()] = $true }

# --- function table, calls per function and instructions ------------------------------------------------------
function Get-Procedures([string] $pdb) {
    $list = [Collections.Generic.List[object]]::new(); $name = $null
    & $pdbutil dump --symbols $pdb 2>$null | ForEach-Object {
        if ($_ -match 'S_[GL]PROC32[^`]*`([^`]+)`') { $name = $Matches[1] }
        elseif ($name -and $_ -match 'addr = 0001:(\d+), code size = (\d+)') { $list.Add([pscustomobject]@{ Name = $name; Start = [int64]$Matches[1]; Size = [int64]$Matches[2] }); $name = $null }
    }
    @($list | Sort-Object Start)
}
function Get-Bodies([string] $dll, $procedures) {
    $bodies = @{}; $i = 0
    foreach ($l in (& $dumpbin /nologo /disasm:nobytes $dll)) {
        if ($l -notmatch '^\s*([0-9A-F]{16}):\s+(\S+)\s*(.*)$') { continue }
        $offset = [Convert]::ToInt64($Matches[1], 16) - 0x180001000L; $op = $Matches[2]; $operands = $Matches[3].Trim()
        while ($i -lt $procedures.Count -and $offset -ge ($procedures[$i].Start + $procedures[$i].Size)) { $i++ }
        if ($i -ge $procedures.Count) { break }
        if ($offset -lt $procedures[$i].Start) { continue }
        $n = $procedures[$i].Name
        if (-not $bodies.ContainsKey($n)) { $bodies[$n] = [Text.StringBuilder]::new() }
        [void]$bodies[$n].Append($op).Append(' ').AppendLine($addressRe.Replace($operands, 'ADDR'))
    }
    $bodies
}
function Convert-BuildDependent([string] $text) {
    # a jump table behind the last ret is disassembled as instructions, its entries are addresses
    if ($text -match '(?m)^jmp r[a-z0-9]+\s*$') { $last = $text.LastIndexOf("ret "); if ($last -ge 0) { $text = $text.Substring(0, $last) } }
    $text = [regex]::Replace($text, '\?\?_C@_[01][0-9A-P]+@[^@\]]*@[^\]\s]*', { param($m) if ($m.Value -match '@([A-Z]\?3\?2|\?\$AA[A-Z]\?\$AA\?3)') { '<path>' } else { $m.Value } })
    $text = $text -replace '\?\?\$bGEnsureHasExecuted@\$0\??[A-P]+@@@', '<ensure>'
    $text = $text -replace '\b1[0-9A-F]{8}h\b', '<data>' -replace '\*4\+0?[0-9A-F]{5,}h\]', '*4+<rva>]' -replace '\?A0x[0-9a-f]+@', '?A0x@'
    $text
}
function Convert-LineNumbers([string] $text) {
    ($text -replace '(mov\s+(r8d|r9d|edx|ecx),)[0-9A-F]+h?\s*$', '$1<n>') -replace '(?m)(mov\s+(r8d|r9d|edx|ecx),)[0-9A-F]+h?\s*$', '$1<n>' -replace '__CpuProfilerEventSpecId\d+', '<scope>'
}

# --- compile commands ---------------------------------------------------------------------------------------
function Get-CompileCommands([string] $pdb) {
    $strings = @{}; $parents = @{}; $lists = @{}; $infos = [Collections.Generic.List[object]]::new(); $cur = $null; $mode = ''
    foreach ($l in (& $pdbutil dump --ids $pdb 2>$null)) {
        if ($l -match '^\s*0x([0-9A-F]+) \| LF_STRING_ID \[size = \d+\] ID: (<no type>|0x([0-9A-F]+)), String: (.*)$') { $strings[$Matches[1]] = $Matches[4]; if ($Matches[3]) { $parents[$Matches[1]] = $Matches[3] }; $mode = '' }
        elseif ($l -match '^\s*0x([0-9A-F]+) \| LF_SUBSTR_LIST') { $cur = $Matches[1]; $lists[$cur] = [Collections.Generic.List[string]]::new(); $mode = 'list' }
        elseif ($l -match '^\s*0x[0-9A-F]+ \| LF_BUILDINFO') { $cur = [Collections.Generic.List[string]]::new(); $infos.Add($cur); $mode = 'info' }
        elseif ($l -match '^\s*0x[0-9A-F]+ \| ') { $mode = '' }
        elseif ($mode -and $l -match '^\s*0x([0-9A-F]+): `') { if ($mode -eq 'list') { $lists[$cur].Add($Matches[1]) } else { $cur.Add($Matches[1]) } }
    }
    function Expand-Id([string] $id) {
        $sb = [Text.StringBuilder]::new()
        if ($parents[$id] -and $lists[$parents[$id]]) { foreach ($e in $lists[$parents[$id]]) { [void]$sb.Append((Expand-Id $e)) } }
        [void]$sb.Append($strings[$id]); $sb.ToString()
    }
    $commands = @{}
    foreach ($i in $infos) {
        if ($i.Count -lt 5) { continue }
        $src = (Expand-Id $i[2]).Replace('\', '/')
        if ($src -match '[^/]+\.(cpp|c)$') { $commands[$Matches[0]] = @([regex]::Split(' ' + (Expand-Id $i[4]), ' (?=-)') | Where-Object { $_ }) }
    }
    $commands
}
function Get-ComparableOption([string] $o) {
    if ($o -match '^-(Fo|Fp|Fd|experimental:log|sourceDependencies|MP|FI)') { return $null }   # outputs, parallelism; force-includes differ by design
    if ($o -notmatch '^-(external:)?I') { return $o }
    $p = ($o -replace '^-(external:)?I', '').Replace('\', '/')
    $p = $p -replace '^.*/(Intermediate/Build/Win64/\w+|Intermediate)/Inc/', 'GENERATED/' -replace '(?i)^.*?/Engine/(Source|Shaders|Plugins)', 'ENGINE/$1'
    "-I$($p -replace "^.*/Source/$ModuleName", "PLUGIN/$ModuleName" -replace '^.*/Plugins/[^/]+/Source$', 'PLUGIN' -replace '^.*/Marketplace/[^/]+/Source$', 'PLUGIN')"
}

# --- sources ------------------------------------------------------------------------------------------------
function Get-LineData([string] $pdb, $procedures, [string] $saveTo) {
    $hashes = @{}; $lines = @{}; $file = $null
    $starts = [int64[]]($procedures | ForEach-Object { $_.Start })
    & $pdbutil dump -l $pdb 2>$null | Out-File -Encoding utf8 $saveTo
    foreach ($l in [IO.File]::ReadLines($saveTo)) {
        if ($l -match '^(\S.*?) \(SHA-256: ([0-9A-Fa-f]+)\)\s*$') { $file = $Matches[1].Trim(); $hashes[$file] = $Matches[2].ToUpper(); continue }
        if (-not $file -or $l -notmatch '^\s+\d+ [0-9A-F]{8}') { continue }
        foreach ($m in [regex]::Matches($l, '(\d+) ([0-9A-F]{8})')) {
            $offset = [Convert]::ToInt64($m.Groups[2].Value, 16)
            $i = [Array]::BinarySearch($starts, $offset); if ($i -lt 0) { $i = (-bnot $i) - 1 }
            if ($i -lt 0 -or $offset -ge $procedures[$i].Start + $procedures[$i].Size) { continue }
            if (-not $lines[$file]) { $lines[$file] = @{} }
            if (-not $lines[$file].ContainsKey($procedures[$i].Name)) { $lines[$file][$procedures[$i].Name] = [int]$m.Groups[1].Value }
        }
    }
    [pscustomobject]@{ Hashes = $hashes; Lines = $lines }
}
function Get-RelativeSource([string] $path) {   # 'Engine\...' or 'Source\<Module>\...' (plugin), the same in both builds
    $p = $path.Replace('/', '\')
    if ($p -match "(?i)\\(Source\\$ModuleName\\.*)$") { return $Matches[1] }
    if ($p -match '(?i)\\(Engine\\(Source|Plugins|Shaders)\\.*)$') { return $Matches[1] }
    $null
}

foreach ($module in $ModuleNames) {
    $ours = Join-Path $IntDir "Bin\$ModulePrefix$module"
    $theirs = Join-Path $DevkitBinaries "$ModulePrefix$module"
    if (-not (Test-Path "$theirs.pdb")) { Write-Host "$module`: the devkit ships no PDB of it, nothing to compare"; continue }
    if (-not (Test-Path "$ours.pdb")) { throw "Build the plugin first ($ours.pdb missing)" }
    Select-PluginModule $module
    $dir = if ($IsMultiModule) { Join-Path $ReportDir $module } else { $ReportDir }
    New-Item -ItemType Directory -Force $dir | Out-Null
    Write-Step "$module`: our build against the devkit's"

    Write-Host '--- layouts'
    $audit = Join-Path $dir 'audit.txt'
    & $tool audit "$ours.pdb" $audit "$theirs.pdb" --renames (Join-Path $SharedConfigDir 'AbiRenames.txt') | Out-Null
    $relevant = Invoke-Bash 'filter-audit.sh' ($audit.Replace('\', '/'))
    Set-Content (Join-Path $dir 'audit-relevant.txt') $relevant
    $excluded = Get-Content (Join-Path $SharedConfigDir 'AbiExclusions.txt') | ForEach-Object { $_.Split('#')[0].Trim() } | Where-Object { $_ }
    $types = @($relevant | Where-Object { $_ -match '^\[[^\]]*\] \w+ (.*): sizeof' } | ForEach-Object { $Matches[1] })
    "differing types: $($types.Count), not excluded: $(@($types | Where-Object { $_ -notin $excluded }).Count)"
    $types | Where-Object { $_ -notin $excluded } | ForEach-Object { "  $_" }

    Write-Host '--- imports and exports'
    foreach ($kind in 'imports', 'exports') {
        $pattern = if ($kind -eq 'exports') { '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)' } else { '^\s+[0-9A-F]+\s+(\?\S+)\s*$' }
        $a = @(& $dumpbin /nologo "/$kind" "$ours.dll" | ForEach-Object { if ($_ -match $pattern) { $Matches[1] } } | Sort-Object -Unique)
        $b = @(& $dumpbin /nologo "/$kind" "$theirs.dll" | ForEach-Object { if ($_ -match $pattern) { $Matches[1] } } | Sort-Object -Unique)
        $d = @(Compare-Object $a $b)
        $onlyOurs = @($d | Where-Object SideIndicator -eq '<=' | ForEach-Object InputObject); $onlyShipped = @($d | Where-Object SideIndicator -eq '=>' | ForEach-Object InputObject)
        "$kind`: ours $($a.Count), shipped $($b.Count), only ours $($onlyOurs.Count), only shipped $($onlyShipped.Count)"
        Set-Content (Join-Path $dir "$kind-only-ours.txt") $onlyOurs; Set-Content (Join-Path $dir "$kind-only-shipped.txt") $onlyShipped
    }

    Write-Host '--- compile commands (<= ours, => shipped)'
    $ca = Get-CompileCommands "$ours.pdb"; $cb = Get-CompileCommands "$theirs.pdb"
    $common = @($ca.Keys | Where-Object { $cb.ContainsKey($_) })
    "objects: ours $($ca.Count), shipped $($cb.Count), in both $($common.Count)"
    $optionDiffs = @{}
    foreach ($k in $common) {
        $x = @($ca[$k] | ForEach-Object { Get-ComparableOption $_ } | Where-Object { $_ }); $y = @($cb[$k] | ForEach-Object { Get-ComparableOption $_ } | Where-Object { $_ })
        foreach ($d in (Compare-Object $x $y)) { $key = "$($d.SideIndicator) $($d.InputObject)"; $optionDiffs[$key] = 1 + [int]$optionDiffs[$key] }
    }
    $optionDiffs.GetEnumerator() | Sort-Object Name | ForEach-Object { '{0,5}x {1}' -f $_.Value, $_.Name }

    Write-Host '--- sources'
    $procOurs = Get-Procedures "$ours.pdb"; $procTheirs = Get-Procedures "$theirs.pdb"
    $la = Get-LineData "$ours.pdb" $procOurs (Join-Path $dir 'lines-ours.txt'); $lb = Get-LineData "$theirs.pdb" $procTheirs (Join-Path $dir 'lines-shipped.txt')
    $ourFiles = @{}; foreach ($f in $la.Hashes.Keys) { $r = Get-RelativeSource $f; if ($r) { $ourFiles[$r] = $f } }
    $same = 0; $notOurs = 0; $differing = [Collections.Generic.List[string]]::new(); $report = [Collections.Generic.List[string]]::new()
    foreach ($f in $lb.Hashes.Keys) {
        $r = Get-RelativeSource $f
        if (-not $r) { continue }
        if ($r -like 'Source\*') { $disk = Join-Path $PluginRoot $r }
        else { $disk = Join-Path $SdkRoot $r }
        if (-not (Test-Path -LiteralPath $disk)) { $notOurs++; continue }
        if ((Get-FileHash -LiteralPath $disk -Algorithm SHA256).Hash -eq $lb.Hashes[$f]) { $same++; continue }
        $differing.Add($r)
        $kind = if ($r -like 'Source\*') { 'plugin ' } elseif ($patchNames.ContainsKey((($r -replace '^Engine\\', '') -replace '\\', '.').ToLowerInvariant() + '.patch')) { 'patched' } else { 'NOPATCH' }
        $shifts = 'no shared out-of-line code'
        if ($ourFiles[$r] -and $la.Lines[$ourFiles[$r]] -and $lb.Lines[$f]) {
            $x = $la.Lines[$ourFiles[$r]]; $y = $lb.Lines[$f]; $segments = [Collections.Generic.List[string]]::new(); $previous = $null
            foreach ($q in (@($x.Keys | Where-Object { $y.ContainsKey($_) } | ForEach-Object { [pscustomobject]@{ Line = $x[$_]; Delta = $y[$_] - $x[$_] } }) | Sort-Object Line)) {
                if ($q.Delta -ne $previous) { $segments.Add("L$($q.Line):$('{0:+0;-0;+0}' -f $q.Delta)"); $previous = $q.Delta }
            }
            if ($segments.Count) { $shifts = $segments -join ' ' }
        }
        $report.Add("$kind  $r  $shifts")
    }
    $report | Sort-Object | Set-Content (Join-Path $dir 'sources-differing.txt')
    "files with code in the shipped PDB: identical $same, differ $($differing.Count), not in our tree $notOurs (list and line shifts in sources-differing.txt)"

    Write-Host '--- functions'
    $ba = Get-Bodies "$ours.dll" $procOurs; $bb = Get-Bodies "$theirs.dll" $procTheirs
    $dupA = @{}; $seen = @{}; foreach ($q in $procOurs) { if ($seen[$q.Name]) { $dupA[$q.Name] = 1 }; $seen[$q.Name] = 1 }
    $dupB = @{}; $seen = @{}; foreach ($q in $procTheirs) { if ($seen[$q.Name]) { $dupB[$q.Name] = 1 }; $seen[$q.Name] = 1 }
    $names = @($ba.Keys | Where-Object { $bb.ContainsKey($_) -and -not $dupA[$_] -and -not $dupB[$_] })
    $identical = 0; $buildOnly = 0; $lineOnly = 0; $real = [Collections.Generic.List[string]]::new()
    foreach ($n in $names) {
        $x = $ba[$n].ToString(); $y = $bb[$n].ToString()
        if ($x -ceq $y) { $identical++; continue }
        $x = Convert-BuildDependent $x; $y = Convert-BuildDependent $y
        if ($x -ceq $y) { $buildOnly++; continue }
        if ((Convert-LineNumbers $x) -ceq (Convert-LineNumbers $y)) { $lineOnly++; continue }
        $real.Add($n)
    }
    "functions: ours $($procOurs.Count), shipped $($procTheirs.Count), compared $($names.Count)"
    "  identical $identical, build paths and addresses only $buildOnly, also line numbers $lineOnly, real differences $($real.Count)"
    $real | Sort-Object | Set-Content (Join-Path $dir 'functions-real.txt')
    $onlyShipped = @($bb.Keys | Where-Object { -not $ba.ContainsKey($_) } | Sort-Object); $onlyOurs = @($ba.Keys | Where-Object { -not $bb.ContainsKey($_) } | Sort-Object)
    "  only in the shipped build $($onlyShipped.Count), only in ours $($onlyOurs.Count)"
    Set-Content (Join-Path $dir 'functions-only-shipped.txt') $onlyShipped; Set-Content (Join-Path $dir 'functions-only-ours.txt') $onlyOurs
    $w1 = [IO.StreamWriter]::new((Join-Path $dir 'bodies-ours.txt')); $w2 = [IO.StreamWriter]::new((Join-Path $dir 'bodies-shipped.txt'))
    foreach ($n in ($real | Sort-Object { $ba[$_].Length })) { $w1.WriteLine("=== $n"); $w1.Write((Convert-BuildDependent $ba[$n].ToString())); $w2.WriteLine("=== $n"); $w2.Write((Convert-BuildDependent $bb[$n].ToString())) }
    $w1.Close(); $w2.Close()
    Write-Host "Report: $dir"
}
