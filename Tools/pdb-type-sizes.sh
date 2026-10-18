#!/usr/bin/env bash
# Usage: pdb-type-sizes.sh <file.pdb>   -> prints "<unique name>\t<sizeof>\t<display name>" for every
# complete (non forward-declared) class/struct/union record in the PDB's TPI stream.
# llvm-pdbutil: $LLVM_PDBUTIL (set by Common.ps1 / Invoke-Bash), else PATH, else the Visual Studio LLVM component
PU="${LLVM_PDBUTIL:-$(command -v llvm-pdbutil)}"
if [ -z "$PU" ]; then
  VSWHERE="$(printenv "ProgramFiles(x86)")/Microsoft Visual Studio/Installer/vswhere.exe"
  VS="$("$VSWHERE" -latest -products "*" -property installationPath 2>/dev/null | tr -d '\r')"
  VS="$(cygpath -u "$VS" 2>/dev/null || echo "$VS")"
  PU="$VS/VC/Tools/Llvm/x64/bin/llvm-pdbutil.exe"
fi
[ -x "$PU" ] || [ -f "$PU" ] || { echo "llvm-pdbutil not found (set LLVM_PDBUTIL)" >&2; exit 1; }
"$PU" dump --types "$1" | awk '
  /\| LF_(CLASS|STRUCTURE|UNION|INTERFACE) \[/ {
    name = $0; sub(/^[^`]*`/, "", name); sub(/`[^`]*$/, "", name); uniq = ""; complete = 0; next_is = 1; next
  }
  next_is && /unique name:/ { uniq = $0; sub(/^[^`]*`/, "", uniq); sub(/`[^`]*$/, "", uniq); next }
  next_is && /field list:/ { complete = ($0 !~ /field list: <no type>/); next }
  next_is && /sizeof [0-9]+/ {
    if (complete && $0 !~ /forward ref/) { n = $0; sub(/.*sizeof /, "", n); print (uniq != "" ? uniq : name) "\t" n "\t" name }
    next_is = 0; next
  }
  /^ +0x[0-9A-F]+ \| / { next_is = 0 }
' | sort -u
