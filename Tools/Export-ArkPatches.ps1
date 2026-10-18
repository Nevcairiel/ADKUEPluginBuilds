<#
.SYNOPSIS
  Exports the differences between the SDK headers and the vanilla UE 5.8 headers as ArkPatches\*.patch.

.DESCRIPTION
  Workflow for changing the ARK header patches: edit headers in SDK\ directly, then run this script.
  Every SDK header that differs from its vanilla counterpart becomes one patch file (named after the header
  path). Patch files of headers that no longer differ are removed. The SDK patch stamp is updated so that
  New-ArkSdk.ps1 does not re-apply the patches onto the already patched files.
#>
param([string[]] $Files)   # optional: relative paths (to the engine root) to export; default: all SDK headers
. "$PSScriptRoot\..\Common.ps1"

$patchDir = Join-Path $BuildDir 'ArkPatches'
New-Item -ItemType Directory -Force $patchDir | Out-Null
if (-not $Files) { $Files = @(Get-AllPluginConfigLines 'SdkHeaders.txt') + @(Get-Content "$SharedConfigDir\SdkHeaders.Extra.txt" -ErrorAction SilentlyContinue) | Sort-Object -Unique }

function Get-PatchName([string] $rel) { ($rel -replace '^Engine[\\/]', '' -replace '[\\/]', '.') + '.patch' }

$written = 0; $removed = 0
foreach ($rel in $Files) {
    $vanilla = Join-Path $EngineRoot $rel
    $patched = Join-Path $SdkRoot $rel
    $patchFile = Join-Path $patchDir (Get-PatchName $rel)
    if (-not (Test-Path $patched)) { continue }
    if ((Get-FileHash $vanilla).Hash -eq (Get-FileHash $patched).Hash) {
        if (Test-Path $patchFile) { Remove-Item $patchFile; $removed++ }
        continue
    }
    # normalize the line endings of the edited header to the vanilla file's style (edits may leave stray CRs)
    $eol = if ([IO.File]::ReadAllText($vanilla).Contains("`r`n")) { "`r`n" } else { "`n" }
    $text0 = [IO.File]::ReadAllText($patched)
    $norm = [regex]::Replace($text0, '\r*\n', $eol)
    if ($norm -ne $text0) { [IO.File]::WriteAllText($patched, $norm) }
    if ((Get-FileHash $vanilla).Hash -eq (Get-FileHash $patched).Hash) {
        if (Test-Path $patchFile) { Remove-Item $patchFile; $removed++ }
        continue
    }
    $relFwd = $rel.Replace('\', '/')
    $diff = & git -c core.autocrlf=false -c core.safecrlf=false diff --no-index --no-color --no-prefix -- $vanilla $patched
    # rewrite the file headers to engine-root relative a/ b/ paths
    $out = foreach ($l in $diff) {
        if ($l -like 'diff --git *') { "diff --git a/$relFwd b/$relFwd" }
        elseif ($l -like '--- *') { "--- a/$relFwd" }
        elseif ($l -like '+++ *') { "+++ b/$relFwd" }
        elseif ($l -like 'index *') { }
        else { $l }
    }
    $text = ($out -join "`n") + "`n"
    if (-not (Test-Path $patchFile) -or (Get-Content $patchFile -Raw) -ne $text) {
        [IO.File]::WriteAllText($patchFile, $text)
        $written++
    }
}

# record the current patch set as applied
$patches = @(Get-ChildItem $patchDir -Filter *.patch | Sort-Object Name)
$stamp = ($patches | ForEach-Object { "$($_.Name):$((Get-FileHash $_.FullName -Algorithm SHA1).Hash)" }) -join "`n"
Set-Content (Join-Path $SdkRoot '.ark-patches') $stamp
$touched = foreach ($p in $patches) { Select-String -Path $p.FullName -Pattern '^\+\+\+ b/(.+)$' | ForEach-Object { $_.Matches[0].Groups[1].Value.Replace('/', '\') } }
Set-Content (Join-Path $SdkRoot '.ark-patched-files') ($touched | Sort-Object -Unique)
Write-Host "Patches: $written written, $removed removed, $($patches.Count) total"
