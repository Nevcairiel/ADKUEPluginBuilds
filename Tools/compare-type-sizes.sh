#!/usr/bin/env bash
# Usage: compare-type-sizes.sh <reference.tsv> <devkit dir with <Module>.tsv files>
# Compares sizeof of every type in the reference (our plugin's PDB) against the devkit PDBs.
# Output columns: status  unique-name  ours  devkit-sizes  modules  display-name
REF="$1"; DIR="$2"
awk -F'\t' '
  FNR == NR { ref[$1] = $2; disp[$1] = $3; next }
  ($1 in ref) {
    m = FILENAME; sub(/.*\//, "", m); sub(/\.tsv$/, "", m)
    key = $1 SUBSEP $2
    if (!(key in seen)) { seen[key] = 1; sizes[$1] = sizes[$1] (sizes[$1] == "" ? "" : ",") $2 }
    if (!(($1, m) in mods)) { mods[$1, m] = 1; modlist[$1] = modlist[$1] (modlist[$1] == "" ? "" : ",") m }
  }
  END {
    for (t in ref) {
      if (!(t in sizes)) { print "MISSING\t" t "\t" ref[t] "\t-\t-\t" disp[t]; continue }
      n = split(sizes[t], s, ",")
      status = "OK"
      if (n > 1) status = "AMBIGUOUS"
      else if (s[1] != ref[t]) status = "DIFF"
      print status "\t" t "\t" ref[t] "\t" sizes[t] "\t" modlist[t] "\t" disp[t]
    }
  }
' "$REF" "$DIR"/[!_]*.tsv | sort
