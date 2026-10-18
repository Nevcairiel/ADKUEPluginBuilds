<#
.SYNOPSIS
  Generates the shared ARK Devkit SDK (headers, generated reflection headers, import libraries) in SDK\.

.DESCRIPTION
  The SDK serves all plugin folders: it covers the union of their captured header lists and link modules.
  1. Copies the engine headers listed in <Plugin>\Build\Config\SdkHeaders.txt (all plugins) and
     Config\SdkHeaders.Extra.txt from the vanilla UE 5.8 tree.
  2. Applies the ARK ABI patches from ArkPatches\*.patch (recovered from the devkit PDBs/binaries).
  3. Runs UnrealHeaderTool over the patched headers so all *.generated.h match the ARK class declarations.
  4. Creates import libraries for every ShooterGameEditor-<Module>.dll any plugin links against.
  The devkit and the UE source tree are only read.
#>
param(
    [switch] $Clean,        # delete the SDK first
    [switch] $HeadersOnly   # skip UHT and import libraries
)
. "$PSScriptRoot\Common.ps1"

if ($Clean -and (Test-Path $SdkRoot)) { Write-Step "Removing $SdkRoot"; Remove-Item -Recurse -Force $SdkRoot }
New-Item -ItemType Directory -Force $SdkRoot, $SharedIntDir | Out-Null

# --- 1. headers ------------------------------------------------------------------------------------------------
Write-Step 'Copying engine headers'
# SdkHeaders.Extra.txt: headers needed only by ARK additions (not part of the vanilla include closure)
$headers = @(Get-AllPluginConfigLines 'SdkHeaders.txt') + @(Get-Content "$SharedConfigDir\SdkHeaders.Extra.txt" -ErrorAction SilentlyContinue) | Sort-Object -Unique
if (-not $headers) { throw 'No plugin has a captured build environment yet: run Tools\Capture-UbtBaseline.ps1 <Plugin> first' }
$copied = 0
foreach ($h in $headers) {
    $src = Join-Path $EngineRoot $h
    $dst = Join-Path $SdkRoot $h
    if (Test-Path $dst) { continue }
    New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
    Copy-Item $src $dst
    (Get-Item $dst).IsReadOnly = $false   # engine sources are read-only; SDK headers get patched
    $copied++
}
Write-Host "  $copied new headers ($($headers.Count) total)"

# --- 2. ARK patches --------------------------------------------------------------------------------------------
# Patches are unified diffs against the vanilla headers (paths relative to the engine root). A stamp file keeps
# track of what was applied; when the patch set changes, the affected files are restored and re-patched.
Write-Step 'Applying ARK patches'
$patchDir = Join-Path $BuildDir 'ArkPatches'
$patches = @(Get-ChildItem $patchDir -Filter *.patch -ErrorAction SilentlyContinue | Sort-Object Name)
$stampFile = Join-Path $SdkRoot '.ark-patches'
$stamp = ($patches | ForEach-Object { "$($_.Name):$((Get-FileHash $_.FullName -Algorithm SHA1).Hash)" }) -join "`n"
$oldStamp = if (Test-Path $stampFile) { Get-Content $stampFile -Raw } else { '' }
if ($stamp.Trim() -ne $oldStamp.Trim()) {
    # restore every file touched by any previously applied or current patch
    $touched = @(foreach ($p in $patches) { Select-String -Path $p.FullName -Pattern '^\+\+\+ b/(.+)$' | ForEach-Object { $_.Matches[0].Groups[1].Value.Replace('/', '\') } })
    $restoreList = Join-Path $SdkRoot '.ark-patched-files'
    if (Test-Path $restoreList) { $touched += @(Get-Content $restoreList) }
    foreach ($f in ($touched | Sort-Object -Unique)) {
        $dst = Join-Path $SdkRoot $f
        Copy-Item (Join-Path $EngineRoot $f) $dst -Force
        (Get-Item $dst).IsReadOnly = $false
    }
    foreach ($p in $patches) {
        $out = & cmd /c "git -c core.autocrlf=false -C `"$SdkRoot`" apply --whitespace=nowarn --ignore-whitespace -p1 `"$($p.FullName)`" 2>&1"
        if ($LASTEXITCODE -ne 0) { throw "Failed to apply $($p.Name):`n$($out -join "`n")" }
        Write-Host "  applied $($p.Name)"
    }
    # patches are stored with LF line endings; restore the line ending style of the vanilla header
    foreach ($f in ($touched | Sort-Object -Unique)) {
        $path = Join-Path $SdkRoot $f
        $eol = if ([IO.File]::ReadAllText((Join-Path $EngineRoot $f)).Contains("`r`n")) { "`r`n" } else { "`n" }
        $text = [IO.File]::ReadAllText($path)
        $norm = [regex]::Replace($text, '\r*\n', $eol)
        if ($norm -ne $text) { [IO.File]::WriteAllText($path, $norm) }
    }
    Set-Content $restoreList ($touched | Sort-Object -Unique)
    Set-Content $stampFile $stamp
} else { Write-Host '  up to date' }
if ($HeadersOnly) { return }

# --- 3. UnrealHeaderTool ---------------------------------------------------------------------------------------
Write-Step 'Running UnrealHeaderTool for engine modules'
& "$BuildDir\Invoke-Uht.ps1" -EngineOnly

# --- 4. import libraries ---------------------------------------------------------------------------------------
Write-Step 'Creating devkit import libraries'
$msvc = Find-Msvc
$modules = Get-AllPluginConfigLines 'EngineModules.txt'
$missing = @($modules | Where-Object { -not (Test-Path (Join-Path $DevkitBinaries "$ModulePrefix$_.dll")) })
if ($missing) { Write-Host "  not in the devkit (no import library): $($missing -join ', ')" }
& "$BuildDir\Tools\New-ImportLibs.ps1" -DevkitBinaries $DevkitBinaries -Modules @($modules | Where-Object { $_ -notin $missing }) `
    -OutDir (Join-Path $SdkRoot 'Lib') -MsvcBin $msvc.Bin -Prefix $ModulePrefix
Write-Step "SDK ready: $SdkRoot"
