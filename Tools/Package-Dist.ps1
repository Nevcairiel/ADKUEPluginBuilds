<#
.SYNOPSIS
  Packages the staged plugins as zips that unpack directly into the devkit root.

.DESCRIPTION
  Writes two sets from Plugins\<Plugin>\Build\Output (never from sources or intermediates):
    Dist\Share\<Name>-<BuildId8>.zip         without PDBs, for sharing
    Dist\WithPdb\<Name>-<BuildId8>-pdb.zip   with PDBs, for your own use: PDBs contain paths of your user profile
  <Name> is <StagedName>-<VersionName>, or the bundle name for a package of several plugins. <BuildId8> is the start of
  the devkit's BuildId; a zip only works with that devkit build. Zips of an earlier run in these folders are replaced.

  BlueprintAssist is not packaged: the devkit ships it, and ours has the same plugin and module names.

  Build and test the plugins first. The script fails if a staged plugin does not match the devkit's BuildId or its last
  build, and reads every zip back: entries and hashes against the staged files, paths, complete plugins, and, for the
  shareable set, that no file contains your user profile path or git name and email.
#>
. "$PSScriptRoot\..\Common.ps1"
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$pluginTargets = @{   # install folder below the devkit root
    AutoSizeComments          = 'Engine/Plugins/Marketplace/AutoSizeComments'
    GitSourceControl          = 'Engine/Plugins/Developer/GitSourceControl'
    ElectronicNodes           = 'Engine/Plugins/Marketplace/ElectronicNodes'
    ue_mcp_bridge             = 'Engine/Plugins/Marketplace/UE_MCP_Bridge'
    ToolsetRegistry           = 'Engine/Plugins/Experimental/ToolsetRegistry'
    JsonUtilitiesEditor       = 'Engine/Plugins/Marketplace/JsonUtilitiesEditor'
    EditorToolset             = 'Engine/Plugins/Experimental/Toolsets/EditorToolset'
    NiagaraToolsets           = 'Engine/Plugins/Experimental/Toolsets/NiagaraToolsets'
    UMGToolSet                = 'Engine/Plugins/Experimental/Toolsets/UMGToolSet'
    SlateInspectorToolset     = 'Engine/Plugins/Experimental/Toolsets/SlateInspectorToolset'
    AnimationAssistantToolset = 'Engine/Plugins/Experimental/Toolsets/AnimationAssistantToolset'
}
$packages = @(   # Name = $null: a single plugin, named after it. The toolsets need ToolsetRegistry and JsonUtilitiesEditor.
    @{ Name = $null; Plugins = @('AutoSizeComments') }
    @{ Name = $null; Plugins = @('GitSourceControl') }
    @{ Name = $null; Plugins = @('ElectronicNodes') }
    @{ Name = $null; Plugins = @('ue_mcp_bridge') }
    @{ Name = 'MCP_Toolsets'; Plugins = @('ToolsetRegistry', 'JsonUtilitiesEditor', 'EditorToolset', 'NiagaraToolsets', 'UMGToolSet', 'SlateInspectorToolset', 'AnimationAssistantToolset') }
)
$variants = @(
    @{ Dir = 'Share';   Suffix = '';     Exclude = @('.zip', '.pdb'); WantPdb = $false }
    @{ Dir = 'WithPdb'; Suffix = '-pdb'; Exclude = @('.zip');         WantPdb = $true }
)

$distDir = Join-Path $BuildDir 'Dist'
$buildId = (Get-Content (Join-Path $DevkitBinaries 'ShooterGameEditor.modules') -Raw | ConvertFrom-Json).BuildId
$buildId8 = $buildId.Substring(0, 8)

# strings that must not be in a shareable zip
$privateStrings = @($env:USERPROFILE, (& git -C $BuildDir config user.name), (& git -C $BuildDir config user.email)) | Where-Object { $_ }
$latin1 = [Text.Encoding]::GetEncoding(28591)
function Find-PrivateString([IO.Compression.ZipArchiveEntry] $entry) {
    $stream = $entry.Open(); $buffer = New-Object IO.MemoryStream; $stream.CopyTo($buffer); $stream.Dispose()
    $text = $latin1.GetString($buffer.ToArray())
    foreach ($s in $privateStrings) {
        $wide = -join ($s.ToCharArray() | ForEach-Object { "$_`0" })
        if ($text.Contains($s) -or $text.Contains($wide)) { "$($entry.Name) contains '$s'" }
    }
}

# staged folders: one per plugin, matching the devkit and the last build
$staged = @{}
foreach ($plugin in $pluginTargets.Keys) {
    $output = Join-Path $PluginsDir "$plugin\Build\Output"
    $dirs = @(Get-ChildItem $output -Directory -ErrorAction SilentlyContinue)
    if ($dirs.Count -ne 1) { throw "$plugin : expected one staged plugin folder in $output, build it first" }
    $bin = Join-Path $dirs[0].FullName 'Binaries\Win64'
    if ((Get-Content (Join-Path $bin 'ShooterGameEditor.modules') -Raw | ConvertFrom-Json).BuildId -ne $buildId) {
        throw "$plugin : the staged plugin was not built for the devkit's BuildId $buildId"
    }
    foreach ($dll in Get-ChildItem $bin -Filter *.dll) {
        $built = Join-Path $PluginsDir "$plugin\Build\Intermediate\Bin\$($dll.Name)"
        if ((Get-FileHash $dll.FullName).Hash -ne (Get-FileHash $built).Hash) { throw "$plugin : $($dll.Name) differs from the last build, run Build.ps1" }
    }
    $staged[$plugin] = $dirs[0]
}

$sha = [Security.Cryptography.SHA256]::Create()
$problems = @()
foreach ($variant in $variants) {
    $variantDir = Join-Path $distDir $variant.Dir
    New-Item -ItemType Directory -Force $variantDir | Out-Null
    Get-ChildItem $variantDir -Filter *.zip -File | Remove-Item
    Write-Step "Dist\$($variant.Dir)"
    foreach ($package in $packages) {
        $items = @()
        foreach ($plugin in $package.Plugins) {
            $src = $staged[$plugin].FullName
            Get-ChildItem $src -Recurse -File | Where-Object { $_.Extension -notin $variant.Exclude } | Sort-Object FullName | ForEach-Object {
                $items += [pscustomobject]@{ Path = $_.FullName; Entry = $pluginTargets[$plugin] + '/' + $_.FullName.Substring($src.Length + 1).Replace('\', '/') }
            }
        }
        $name = $package.Name
        if (-not $name) {
            $dir = $staged[$package.Plugins[0]]
            $descriptor = Get-ChildItem $dir.FullName -Filter *.uplugin | Select-Object -First 1
            $name = "$($dir.Name)-$((Get-Content $descriptor.FullName -Raw | ConvertFrom-Json).VersionName)"
        }
        $zipPath = Join-Path $variantDir "$name-$buildId8$($variant.Suffix).zip"
        $zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
        foreach ($item in $items) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $item.Path, $item.Entry, 'Optimal') }
        $zip.Dispose()

        # read the zip back
        $found = @()
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        if ($zip.Entries.Count -ne $items.Count) { $found += "$($zip.Entries.Count) entries instead of $($items.Count)" }
        foreach ($item in $items) {
            $entry = $zip.GetEntry($item.Entry)
            if (-not $entry) { $found += "missing $($item.Entry)"; continue }
            $stream = $entry.Open(); $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', ''); $stream.Dispose()
            if ($hash -ne (Get-FileHash $item.Path -Algorithm SHA256).Hash) { $found += "wrong content: $($item.Entry)" }
        }
        $prefixes = @($package.Plugins | ForEach-Object { $pluginTargets[$_] + '/' })
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.Contains('\') -or -not ($prefixes | Where-Object { $entry.FullName.StartsWith($_) })) { $found += "bad path: $($entry.FullName)" }
        }
        $dllCount = 0; $pdbCount = 0
        foreach ($prefix in $prefixes) {
            $own = @($zip.Entries | Where-Object { $_.FullName.StartsWith($prefix) })
            $dlls = @($own | Where-Object { $_.Name -like '*.dll' }).Count
            $pdbs = @($own | Where-Object { $_.Name -like '*.pdb' }).Count
            $dllCount += $dlls; $pdbCount += $pdbs
            if ($dlls -lt 1 -or -not ($own | Where-Object { $_.Name -like '*.uplugin' }) -or -not ($own | Where-Object { $_.Name -eq 'ShooterGameEditor.modules' })) { $found += "incomplete plugin: $prefix" }
            if ($pdbs -ne $(if ($variant.WantPdb) { $dlls } else { 0 })) { $found += "$pdbs PDBs for $dlls DLLs: $prefix" }
        }
        if (-not $variant.WantPdb) { foreach ($entry in $zip.Entries) { if ($entry.Name -notlike '*.png') { $found += @(Find-PrivateString $entry) } } }
        $zip.Dispose()

        '{0,-48} {1,8:N1} MB  {2} plugin(s), {3} files, {4} DLLs, {5} PDBs' -f (Split-Path -Leaf $zipPath), ((Get-Item $zipPath).Length / 1MB), $package.Plugins.Count, $items.Count, $dllCount, $pdbCount
        $problems += $found | ForEach-Object { "$(Split-Path -Leaf $zipPath): $_" }
    }
}
if ($problems) { $problems | ForEach-Object { Write-Host $_ -ForegroundColor Red }; throw "$($problems.Count) problem(s) in the zips" }
Write-Step "All zips verified, BuildId $buildId"
