#!/usr/bin/env bash
# Compile gate for BreakBox.
#
# Two things have to hold and neither tool checks both on its own:
#
#   1. The pure files (Types/Core/Exits) must build with no NinjaTrader
#      assemblies at all — that is what `dotnet run --project tests` proves,
#      and it runs the assert suite while it is there.
#
#   2. All NT8 files must build TOGETHER against NT8's references. `nt8c check`
#      is per-file and cannot see a sibling's types, so it reports CS0246 on
#      every cross-file reference (VeeSnapCore.cs, in production, fails the same
#      way). `nt8c build` compiles the whole Custom/ tree and drowns in ~2100
#      pre-existing errors from NT8's own @-prefixed samples.
#
# So this concatenates all of them into one compilation unit — usings hoisted
# to the top, which is where C# requires them — and checks that. It is the
# closest thing to NT8's own F5 that runs from the shell.
#
# Usage: scripts/check.sh          (from the repo root)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/ninjascript"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

FILES=(BreakBoxTypes BreakBoxCloud BreakBoxCore BreakBoxExits BreakBoxHistory AveragingEngineCore BreakBoxStrategy BreakBoxPanel BreakBoxVision)
COMBINED="$TMP/BreakBoxCombined.cs"

# Usings first, de-duplicated, then every file with its own usings stripped.
for f in "${FILES[@]}"; do grep -hE '^\s*using [A-Za-z]' "$SRC/$f.cs"; done | sed 's/^\s*//' | sort -u > "$COMBINED"
for f in "${FILES[@]}"; do
  echo ""
  echo "// ===== $f.cs ====="
  grep -vE '^\s*using [A-Za-z]' "$SRC/$f.cs"
done >> "$COMBINED"

echo "== 1/2  pure engines + assert suite"
dotnet run --project "$ROOT/tests" | tail -3

echo ""
echo "== 2/2  all NT8 files against NT8 references"
if nt8c check "$COMBINED" 2>&1 | tee "$TMP/out.txt" | grep -q "error CS"; then
  sed "s#$COMBINED#BreakBoxCombined.cs#" "$TMP/out.txt"
  exit 1
fi
echo "  compiles clean"
