#!/usr/bin/env bash
# Usage: filter-audit.sh <abi-audit.txt>
# Keeps only ABI-relevant findings of a PdbLayout audit: data layout changes, vtable (introduced virtual)
# changes, enumerator changes, access/virtual/static changes of existing methods and added/removed overrides of
# virtual functions. Pure (non-virtual) method additions
# and size changes fully explained by member types are dropped; so are anonymous types.
awk '
  function flush() {
    if (entry != "" && keep && entry !~ /<unnamed-(tag|type)/) printf "%s\n", entry
    entry = ""; keep = 0; section = ""
  }
  /^\[/ { flush(); entry = $0; next }
  entry == "" { next }
  /^  [a-z].*:$/ { section = $0 }
  {
    entry = entry "\n" $0
    if (section ~ /^  (data|bases|size|introduced virtuals|enumerators)/ && $0 ~ /^    [+-] /) keep = 1
    if (section ~ /^  methods/ && $0 ~ /^    ~ /) keep = 1
    # added / removed overrides of base class virtuals: they are the vtable entries of classes derived in a plugin
    if (section ~ /^  methods/ && $0 ~ /^    [+-] method/ && $0 ~ / virtual / && $0 !~ /intro|~|__vecDelDtor|_getUObject|compiler-generated/) keep = 1
  }
  END { flush() }
' "$1"
