#!/usr/bin/env bash
# Compile gate for BreakBox. Two things have to hold and neither tool checks both.
#
#   1. The pure files (Types/Core/Exits/Cloud/History/Averaging) must build with no
#      NinjaTrader assemblies at all — that is what `dotnet run --project tests`
#      proves, and it runs the assert suite while it is there.
#   2. The NinjaScript files must build the way NinjaTrader actually builds them:
#      every .cs under Custom/ into ONE assembly, each file keeping ITS OWN using
#      directives. `nt8c build --custom-dir` does exactly that, with NT8's own
#      compiler and reference set.
#
# WHY THIS REPLACED THE OLD CONCATENATION, AND WHY THE OLD GATE WAS WORSE THAN
# NOTHING. The previous version built one combined .cs and tested it through a pipe:
#
#     if nt8c check "$COMBINED" 2>&1 | tee "$TMP/out.txt" | grep -q "error CS"; then
#
# under `set -o pipefail`. `nt8c` exits 5 when it finds errors, so a FAILING compile
# made the pipeline non-zero and the condition FALSE. And with no errors, `grep -q`
# exits 1, so the pipeline was non-zero then too. There was NO input for which the
# error branch ran: that half of the gate was dead code that always printed
# "compiles clean". The identical construct shipped a NinjaScript file with 10 real
# CS0104 errors straight into NinjaTrader from a sibling project.
#
# The concatenation itself was also wrong, in the opposite direction. Hoisting every
# file's usings into one block SHARES them across files that never imported them:
# only BreakBoxPanel.cs imports System.Windows.Shapes, and that leak made
# BreakBoxVision.cs:465 `Path.Combine` report a phantom CS0104 against System.IO.Path
# that NinjaTrader never sees. A gate that invents errors gets ignored, which is the
# same as not having one.
#
# Never test a compiler through a pipe. Capture, then read the exit code AND the body.
#
# Usage: scripts/check.sh   (from anywhere)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/ninjascript"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
STAGE="$WORK/Custom"

echo "== 1/2  pure engines + assert suite"
dotnet run --project "$ROOT/tests" | tail -3

# Staged by NAMESPACE, which is what decides the folder in a real deployment —
# not by the file's role, and not by a hand-kept list that goes stale the first
# time someone adds a file.
mkdir -p "$STAGE/Strategies" "$STAGE/Indicators"
EXPECTED=0
for f in "$SRC"/*.cs; do
    if grep -q '^namespace NinjaTrader\.NinjaScript\.Indicators' "$f"; then
        cp "$f" "$STAGE/Indicators/"
    else
        cp "$f" "$STAGE/Strategies/"
    fi
    EXPECTED=$((EXPECTED + 1))
done

echo ""
echo "== 2/2  all NinjaScript files as one assembly, NT8-style"

set +e
nt8c build --custom-dir "$STAGE" --no-emit --agent > "$WORK/out.json" 2>&1
rc=$?
set -e

python3 - "$WORK/out.json" "$rc" "$EXPECTED" <<'PY'
import json, sys
raw = open(sys.argv[1]).read()
rc, expected = int(sys.argv[2]), int(sys.argv[3])
try:
    d = json.loads(raw)
except Exception:
    # A hard nt8c failure prints a bare `error: ...` line, not JSON. Reading an
    # unparseable body as zero errors is how a gate goes quietly blind.
    print(raw.strip())
    print("  FAILED (nt8c exit %d, no JSON)" % rc)
    sys.exit(1)

errs = d.get("results", {}).get("errors", []) or []
n = d.get("meta", {}).get("files_compiled", 0)

# Only BreakBox files are staged, so every error here is ours. No ownership filter
# to keep in sync, and therefore no blind spot for a file added later.
print("  files compiled: %s   errors: %d" % (n, len(errs)))
for e in errs[:40]:
    print("    %s(%s,%s): %s %s" % (e.get("file", "?").split("/")[-1], e.get("line"),
                                    e.get("col"), e.get("code"), e.get("message")))

if n != expected:
    print("  FAILED: staged %d files but nt8c compiled %s. A vacuous green is not a pass."
          % (expected, n))
    sys.exit(1)
if errs:
    print("  FAILED (nt8c exit %d)" % rc)
    sys.exit(1)
print("  compiles clean")
PY
