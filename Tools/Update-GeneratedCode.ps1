<#
.SYNOPSIS
  Rewrites UHT output of the plugin so that reflection flag literals use the ARK Devkit's enum values.

.DESCRIPTION
  UHT (vanilla UE 5.8) writes property flags as numeric literals, e.g. (EPropertyFlags)0x0114000000000005.
  The ARK engine renumbered several EPropertyFlags bits (recovered from ShooterGameEditor-CoreUObject.pdb):

      flag                                 vanilla bit   ARK bit
      CPF_TObjectPtr                            56          59
      CPF_ExperimentalOverridableLogic          57          60
      CPF_ExperimentalAlwaysOverriden           58          61
      CPF_ExperimentalNeverOverriden            59          62
      CPF_AllowSelfReference                    60          63
      CPF_ForcePostConstructLink                61          23
  (ARK uses the freed bits 56..58 for CPF_ForcedNoLocalization, CPF_LiveTunable and CPF_SaveData, bit 22 for
  CPF_Primal_CDOOnlyData.) All other reflection flag enums (EFunctionFlags, EClassFlags, EStructFlags,
  EObjectFlags, EClassCastFlags, EPackageFlags, EEnumFlags) keep their vanilla values.
#>
param([Parameter(Mandatory)] [string] $GeneratedDir)
$ErrorActionPreference = 'Stop'

$bitMap = @{ 56 = 59; 57 = 60; 58 = 61; 59 = 62; 60 = 63; 61 = 23 }

function Convert-PropertyFlags([UInt64] $v) {
    [UInt64] $r = 0
    for ($b = 0; $b -lt 64; $b++) {
        if (($v -band ([UInt64]1 -shl $b)) -ne 0) {
            $to = if ($bitMap.ContainsKey($b)) { $bitMap[$b] } else { $b }
            $r = $r -bor ([UInt64]1 -shl $to)
        }
    }
    return $r
}

$pattern = [regex]'\(EPropertyFlags\)0x([0-9A-Fa-f]{1,16})'
foreach ($f in Get-ChildItem $GeneratedDir -Include *.gen.cpp, *.generated.h -Recurse) {
    $text = [IO.File]::ReadAllText($f.FullName)
    if ($text.Contains('// ARK: EPropertyFlags remapped')) { continue }   # already converted (UHT rewrites changed files only)
    $count = 0
    $new = $pattern.Replace($text, {
        param($m)
        $v = [UInt64]::Parse($m.Groups[1].Value, 'HexNumber')
        foreach ($b in 22, 23, 62, 63) {
            if (($v -band ([UInt64]1 -shl $b)) -ne 0) { throw "Unexpected vanilla property flag bit $b in $($f.Name)" }
        }
        $script:count++
        '(EPropertyFlags)0x{0:X16}' -f (Convert-PropertyFlags $v)
    })
    if ($count -gt 0 -or $f.Name -like '*.gen.cpp') {
        $new = "// ARK: EPropertyFlags remapped to ARK Devkit values by Update-GeneratedCode.ps1 ($count literals)`r`n" + $new
        [IO.File]::WriteAllText($f.FullName, $new)
    }
}
