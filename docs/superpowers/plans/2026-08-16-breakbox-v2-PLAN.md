# BreakBox v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace BreakBox v1 — which took zero trades — with a cloud-reclaim signal engine, a
correctly-scaled box engine, a vertical diagnostic panel, and cross-session trade history.

**Architecture:** Two pure signal engines (`BbCloud`, `BbEngine`) return the same `BbAction` and feed
the same already-tested `BbExits` bracket. A thin NinjaScript shell owns orders, session, governor
and the seconds→bars conversion that makes the model bar-size independent. The panel renders a gate
ladder from whichever engine would act next, so "why is it not trading" is answerable at a glance.

**Tech Stack:** C# 7.3 · NinjaTrader 8 (.NET Framework 4.8) · WPF (`UserControlCollection`) ·
a hand-rolled net8 assert runner for the pure files · `nt8c` for the NT8 compile gate.

**Spec:** [`docs/superpowers/specs/2026-08-16-breakbox-v2-design.md`](../specs/2026-08-16-breakbox-v2-design.md)

---

## Global Constraints

Every task's requirements implicitly include this section.

- **Pure files** (`BreakBoxTypes.cs`, `BreakBoxCore.cs`, `BreakBoxCloud.cs`, `BreakBoxExits.cs`,
  `BreakBoxHistory.cs`): **zero `using NinjaTrader.*`**, namespace `BreakBoxCore`, **C# 7.3 only**.
  They compile inside `tests/BreakBox.Tests.csproj`, which has no NT8 assemblies on its reference
  path. A `using NinjaTrader` in one of them is how a CS0101 duplicate-type clash starts.
- **File I/O is not pure.** It lives in `BreakBoxStrategy.cs`. `BreakBoxHistory.cs` holds the record
  type, a hand-rolled serialiser/parser and the equity reduction — nothing that touches a disk.
- **No JSON library.** NT8 is .NET Framework 4.8 / C# 7.3; the test runner is net8. No serializer
  sits on both reference paths. Serialisation is hand-rolled.
- **No horizon is expressed in bars on the `NinjaScriptProperty` surface.** Every one is seconds,
  converted through `BbScale.Bars(secs, barSec, min)` inside `BuildConfigs()`. This is the contract
  that fixes v1's root cause, and `BuildConfigs()` — not `State.DataLoaded` — is where the
  conversion runs, because every panel toggle calls it again.
- **Order APIs only from `OnBarUpdate` or strategy-thread events**, never from `OnMarketData` and
  never from a WPF click handler. Panel actions cross back via `TriggerCustomEvent`.
  See memory `[[nt8-orders-from-marketdata-thread-crash]]`.
- **The order-event race rules the shell.** Every in-flight flag is written *before* its submit;
  every handler clears on the signal **name**. See memory `[[nt8-order-event-race]]`.
- **Tests** are asserts in the existing hand-rolled harness (`tests/Program.cs`, `T.Check`,
  `T.CheckClose`, `T.CheckInt`). **No test framework, no fixtures.** Each new test file registers
  itself by *adding one line* to `Program.Main` — never by replacing the block.
- **The gate is `scripts/check.sh`** and both halves must be clean: `dotnet run --project tests`
  (all asserts pass) and all NT8 files concatenated into one compilation unit checked with `nt8c`
  (0 errors, 0 warnings). `nt8c check` on a *single* file reports false CS0246 on every cross-file
  type — that is expected and is why the gate concatenates.
- **Deploy is part of done.** A task is not complete until the `.cs` files are in
  `…/NinjaTrader 8/bin/Custom/Strategies/`, verified with `cmp`. See memory
  `[[nt8-deploy-copy-files]]`.
- **Commit messages** use a conventional prefix and end with
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- **Clean room.** This rebuild is from black-box observation of a competitor product. No
  decompiling, no reuse of their names, order prefixes (`A_`, `B_`) or branding. **This repo stays
  private.** Order signal names are ours: `BB_CloudLong`, `BB_CloudShort`, `BB_BoxLong`,
  `BB_BoxShort`.
- **Nothing here is validated.** Every default is provisional. No number produced by this code means
  anything until §13 of the spec has been run — a counting pass with orders disabled, then a
  random-entry control.

---

## File Structure

| File | Responsibility | Pure? | Phase |
|---|---|---|---|
| `ninjascript/BreakBoxTypes.cs` | bars, swings, ATR, EMA, rounding, window math, **`BbScale`**, **`BbGateReport`**, **`BbMath.CloseInRange`** | yes | 1 |
| `ninjascript/BreakBoxCloud.cs` | **NEW** — `BbCloudConfig`, `BbCloudState`, `BbCloud`: regime latch, pullback token, gold-candle gates | yes | 2 |
| `ninjascript/BreakBoxCore.cs` | **REWRITTEN** — box lifecycle (form → seal → invalidate), validity against the box-range distribution | yes | 3 |
| `ninjascript/BreakBoxExits.cs` | the bracket — **unchanged**; only its `BreakevenOnTp1` default is discussed | yes | — |
| `ninjascript/BreakBoxHistory.cs` | **NEW** — `BbTradeRecord`, serialise/parse, cumulative equity | yes | 4 |
| `ninjascript/BreakBoxStrategy.cs` | series, indicators, orders, session, governor, scaling, arbitration, history file I/O | no | 1–4 |
| `ninjascript/BreakBoxPanel.cs` | **REWRITTEN** — vertical sidebar, gate ladder, engine log, history chart | no | 4 |
| `ninjascript/BreakBoxVision.cs` | **NEW** — indicator: cloud, boxes, gold candles, markers. Trades nothing | no | 5 |
| `tests/ShellTests.cs` | **NEW** — `BbScale`, `BbGateReport`, the shell-owned pure types | — | 1 |
| `tests/CloudTests.cs` | **NEW** — regime latch, token, gold-candle gates | — | 2 |
| `tests/BoxTests.cs` | **REWRITTEN** — box lifecycle and cold start | — | 3 |
| `tests/HistoryTests.cs` | **NEW** — round-trip, equity, the write guard | — | 4 |
| `tests/BracketTests.cs` | **one case corrected** (13 lots 7/6, not 10 lots 7/3); the other two fidelity tests are untouchable | — | 4 |

---

## Phases

Each phase ends with working, testable software and is a legitimate stopping point.

| # | Plan | Delivers | Tasks |
|---|---|---|---|
| 1 | [`01-scaling-and-shell-fixes`](2026-08-16-breakbox-v2-01-scaling-and-shell-fixes.md) | `BbScale`, `BbGateReport`, and the 14 verified shell fixes. The v1 box engine starts producing boxes | 1–11 |
| 2 | [`02-cloud-engine`](2026-08-16-breakbox-v2-02-cloud-engine.md) | `BreakBoxCloud.cs` + arbitration + deploy. **The primary signal** | 20–28 |
| 3 | [`03-box-engine`](2026-08-16-breakbox-v2-03-box-engine.md) | `BreakBoxCore.cs` rewritten; the slot machinery deleted (closes B11, B12) | 40–49 |
| 4 | [`04-history-and-panel`](2026-08-16-breakbox-v2-04-history-and-panel.md) | Cross-session history + the vertical panel | 60–69 |
| 5 | [`05-vision-indicator`](2026-08-16-breakbox-v2-05-vision-indicator.md) | `BreakBoxVision.cs` — the side-by-side comparison against the reference | 80–87 |

**Phase 1 must land first** — every later phase converts its dials through `BbScale` and writes its
gate report through `BbGateReport`. Phases 2 and 3 are independent of each other. Phase 4 needs 2
and 3 for the gate ladder's row labels. Phase 5 needs 4 for the history-driven markers.

### The order that matters most

If time is short, **Phase 1 → Phase 2 → Phase 5** is the shortest path to answering the question the
user actually asked: *does this look and behave like the product I showed you?* Phase 5's indicator
answers it on a chart in an afternoon, with no risk and no Replay sessions spent.

---

## After the plan

Implementing every task leaves a strategy that compiles, deploys and trades. It leaves **nothing
validated**. Spec §13 is the next piece of work and it is not optional:

1. **Count only** — orders disabled, one counter per gate, 5–10 Replay sessions, *then* tune.
2. **Random-entry control** — on `NQData`, through the identical bracket. Not separable from
   random → does not proceed.
3. **Freeze the M-tagged parameters**, tune at most three G parameters, pre-register the rest.

Gates: `[[strategy-profitability-gates]]`, out-of-sample only, never the vendor's numbers.
## Phase 1 — Scaling foundation and shell fixes

### Task 1: `BbScale.Bars` — the seconds→bars conversion

**Files:**
- Modify: `ninjascript/BreakBoxTypes.cs:214` (insert a new static class between `BbMath`'s closing brace and the namespace's)
- Create: `tests/ShellTests.cs`
- Modify: `tests/Program.cs:45-54`
- Test: `tests/ShellTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class BbScale` in namespace `BreakBoxCore`, with `public const int FallbackSeconds = 30;`, `public const int MinEstimateSamples = 200;` and `public static int Bars(int horizonSecs, int barSec, int min)`. Every later phase converts its seconds dials through this and nothing else.

- [ ] **Step 1: Write the failing test**

Create `tests/ShellTests.cs`:

```csharp
// ShellTests — the pure types the SHELL leans on, as opposed to the ones an
// engine owns: the seconds->bars scaler that makes the §8 "escala sola"
// contract true, and the gate report the panel renders.
//
// They live in their own file because neither belongs to an engine — every
// engine and the strategy itself go through them — and because a bug in either
// is invisible on a chart. A horizon silently floored to one bar still trades;
// it just trades a different model than the one that was measured.
using System;
using BreakBoxCore;

public static class ShellTests
{
    public static void Run()
    {
        SecondsToBars();
    }

    // §8: every horizon on the parameter surface is SECONDS, and this is the
    // only conversion in the codebase. The reference model was measured on 30s
    // bars, so those rows have to come out at exactly the periods §5.4 names.
    private static void SecondsToBars()
    {
        T.Section("Scale — seconds to bars (spec 8)");

        T.CheckInt(BbScale.Bars(300, 30, 2), 10, "RibbonFastSec 300 @30s = EMA(10)");
        T.CheckInt(BbScale.Bars(690, 30, 2), 23, "RibbonSlowSec 690 @30s = EMA(23)");
        T.CheckInt(BbScale.Bars(1560, 30, 2), 52, "TrendLineSec 1560 @30s = EMA(52)");

        // The same dial on three other bar sizes. This is the entire point of
        // the contract: the model does not change, the bar counts do.
        T.CheckInt(BbScale.Bars(1560, 15, 2), 104, "TrendLineSec @15s");
        T.CheckInt(BbScale.Bars(1560, 60, 2), 26, "TrendLineSec @1m");
        T.CheckInt(BbScale.Bars(1560, 120, 2), 13, "TrendLineSec @2m");

        // Truncation, not rounding. 120s of trigger life on a 90s bar is ONE
        // bar; rounding it to 2 hands the trade a longer leash than was asked
        // for, and the direction of that error is always "riskier".
        T.CheckInt(BbScale.Bars(120, 90, 1), 1, "the division truncates");

        // The floor is the defect guard, not politeness. A horizon shorter than
        // one bar converts to zero, and a zero-bar gate is not strict — it is
        // OFF: `ageBars >= 0` is true on the touch bar itself, which is the one
        // bar §5.2 step 3 exists to exclude.
        T.CheckInt(BbScale.Bars(30, 300, 1), 1, "a sub-bar horizon floors at the minimum");
        T.CheckInt(BbScale.Bars(30, 300, 2), 2, "and honours a minimum of 2");
        T.CheckInt(BbScale.Bars(600, 0, 2), 2, "a nonsense bar size floors rather than dividing by zero");
    }
}
```

And register it in `tests/Program.cs`. This block is the BASE of the run list, not a
template: every later phase that adds a suite **inserts one line** here and never
rewrites the block. Three tasks across three phases each replacing it wholesale is how
~40 asserts stop running with nothing red to show for it.

```csharp
    public static int Main()
    {
        BoxTests.Run();
        BracketTests.Run();
        ShellTests.Run();
        // Phase 4 inserts HistoryTests.Run() after this line; Phase 5 inserts
        // VisionTests.Run() after that. Insert, never replace.
        Console.WriteLine();
        Console.WriteLine(T.Failures == 0
            ? "ALL PASS (" + T.Checks + " checks)"
            : T.Failures + " FAILURES of " + T.Checks + " checks");
        return T.Failures == 0 ? 0 : 1;
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: compile failure — `error CS0103: The name 'BbScale' does not exist in the current context`

- [ ] **Step 3: Write minimal implementation**

In `ninjascript/BreakBoxTypes.cs`, insert between `BbMath`'s closing brace (line 214) and the namespace's closing brace (line 215):

```csharp

    // Seconds -> bars, and the bar-size estimate for non-time series. It lives
    // in Types because BOTH the strategy and its config builder need it and
    // neither may own it: the §8 contract is that no horizon is ever expressed
    // in bars on the parameter surface, so every dial passes through here on its
    // way in. v1 died of the opposite arrangement — a 240-MINUTE box range
    // divided by a 14-BAR ATR — and no tuning fixes a dimensional error.
    public static class BbScale
    {
        // What a non-time series falls back to when there is too little history
        // to estimate anything. 30s is the bar size the whole model was measured
        // on, so a wrong fallback is at least the right wrong number.
        public const int FallbackSeconds = 30;
        public const int MinEstimateSamples = 200;

        // A horizon in seconds, on a `barSec` series, floored at `min` bars.
        public static int Bars(int horizonSecs, int barSec, int min)
        {
            if (min < 1)
                min = 1;
            if (barSec < 1 || horizonSecs < 1)
                return min;
            int n = horizonSecs / barSec;
            return n < min ? min : n;
        }
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: `ALL PASS (N checks)`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxTypes.cs tests/ShellTests.cs tests/Program.cs && \
git commit -m "feat(scale): BbScale.Bars, the one seconds-to-bars conversion" \
  -m "Spec 8. No horizon is expressed in bars on the parameter surface; every dial converts here, floored so a sub-bar horizon cannot silently turn a gate off." \
  -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `BbScale.EstimateBarSeconds` — tick / volume / range bars

**Files:**
- Modify: `ninjascript/BreakBoxTypes.cs` (inside `BbScale`, after `Bars`)
- Modify: `tests/ShellTests.cs` (add to `Run()`)
- Test: `tests/ShellTests.cs`

**Interfaces:**
- Consumes: `BbScale.MinEstimateSamples`.
- Produces: `public static int EstimateBarSeconds(double[] gapSecs, int count)` — returns the median seconds per bar, or **0** meaning "no answer", which is the caller's cue to fall back to `FallbackSeconds` **and say so**.

- [ ] **Step 1: Write the failing test**

In `tests/ShellTests.cs`, add `BarSecondsEstimate();` to `Run()` and the method:

```csharp
    // §8's non-time-series branch. Javier runs 150-tick charts elsewhere in this
    // workspace (PatternZone), so a hard throw would turn "escala sola" into "no
    // carga". The estimate makes a tick chart usable and VISIBLY approximate
    // instead of silently wrong.
    private static void BarSecondsEstimate()
    {
        T.Section("Scale — non-time bar size estimate (spec 8)");

        // 150-tick NQ during RTH: a bar every ~12 seconds.
        double[] g = new double[400];
        for (int i = 0; i < g.Length; i++) g[i] = 12.0;
        T.CheckInt(BbScale.EstimateBarSeconds(g, g.Length), 12, "a clean 150-tick sample estimates 12s");

        // Median, not mean, and this is why. Overnight the same chart prints one
        // bar an hour; twenty of those gaps move a MEAN of 400 samples by ~180s,
        // which would have a 12-second chart claim it is on 3-minute bars and
        // stretch every horizon 15x. The median does not move at all.
        for (int i = 0; i < 20; i++) g[i] = 3600.0;
        T.CheckInt(BbScale.EstimateBarSeconds(g, g.Length), 12, "session gaps do not move the median");

        // Too little history to answer. 0 means "no estimate" so the caller can
        // fall back AND warn; guessing off 50 bars is how a chart ends up
        // silently running a different model.
        T.CheckInt(BbScale.EstimateBarSeconds(g, 199), 0, "under 200 samples yields no estimate");
        T.CheckInt(BbScale.EstimateBarSeconds(null, 400), 0, "no history yields no estimate");

        // Sub-second bars are a real configuration (a fast range chart). They
        // must not collapse to 0 and take every horizon's divisor with them.
        double[] fast = new double[250];
        for (int i = 0; i < fast.Length; i++) fast[i] = 0.2;
        T.CheckInt(BbScale.EstimateBarSeconds(fast, fast.Length), 1, "a sub-second series floors at 1s");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: compile failure — `error CS0117: 'BbScale' does not contain a definition for 'EstimateBarSeconds'`

- [ ] **Step 3: Write minimal implementation**

In `ninjascript/BreakBoxTypes.cs`, inside `BbScale`, after `Bars`:

```csharp

        // Median seconds per bar over `count` consecutive inter-bar gaps.
        // Returns 0 for "cannot answer" — the caller falls back to
        // FallbackSeconds and prints that it did, because an approximate bar
        // size that announces itself is fine and one that does not is a lie the
        // whole parameter surface is built on.
        public static int EstimateBarSeconds(double[] gapSecs, int count)
        {
            if (gapSecs == null || count < MinEstimateSamples || count > gapSecs.Length)
                return 0;

            // Copy before sorting: the caller's buffer is its own history and
            // must survive being measured.
            double[] s = new double[count];
            Array.Copy(gapSecs, s, count);
            Array.Sort(s);

            double m = (count & 1) == 1
                ? s[count / 2]
                : 0.5 * (s[count / 2 - 1] + s[count / 2]);

            int secs = (int)Math.Floor(m + 0.5);
            return secs < 1 ? 1 : secs;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: `ALL PASS (N checks)`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxTypes.cs tests/ShellTests.cs && \
git commit -m "feat(scale): estimate bar seconds for tick/volume/range series" \
  -m "Median so one overnight gap cannot restate a 12s chart as 3m. Returns 0 rather than guessing off thin history, so the caller falls back loudly." \
  -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `BarSeconds()` in the shell, and the conversion inside `BuildConfigs()`

**Files:**
- Modify: `ninjascript/BreakBoxStrategy.cs:114-120` (fields), `:188` (default), `:239-258` (DataLoaded), `:272-299` (BuildConfigs), `:506-512` (AgeWorkingEntry), `:908-910` (the bar-valued property)
- Modify: `ninjascript/BreakBoxCore.cs:104` (the config field's name), `:235` (its one reader)
- Modify: `tests/BoxTests.cs:164`, `:202` (the same rename)
- Test: `scripts/check.sh` + a grep conformance gate (the members are NT8-bound: `BarsPeriod`, `Bars.GetTime`, `Print` have no counterpart in the net8 runner, so the assert suite cannot reach them — the pure half is already covered by Tasks 1-2)

**Interfaces:**
- Consumes: `BbScale.Bars`, `BbScale.EstimateBarSeconds`, `BbScale.FallbackSeconds`, `BbScale.MinEstimateSamples`.
- Produces: `private int BarSeconds();`, `private int _barSec;`, `private string _barSecLabel;` (the panel's SESSION block reads the label later), and the rule that **`BuildConfigs()` performs every seconds→bars conversion**. New NinjaScriptProperty `TriggerLifeSec` replaces `TriggerLifeBars`, and `BbConfig.TriggerLifeBars` is renamed to **`BbConfig.TriggerLife`** — the field is a bar count that nothing outside `BuildConfigs` may write, and carrying "Bars" in its name is what let v1's surface and v1's config share a spelling and drift apart. Every later phase reads `_cfg.TriggerLife`; the token `TriggerLifeBars` is gone from the tree after this task.

- [ ] **Step 1: Write the failing test**

The conformance gate — the bar-valued spelling may not survive anywhere, on the surface or in the config:

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
grep -rn 'TriggerLifeBars' ninjascript/ tests/
```

- [ ] **Step 2: Run test to verify it fails**

Run: the command above
Expected: eight hits — `BreakBoxStrategy.cs:188 TriggerLifeBars = 5;`, `:290 _cfg.TriggerLifeBars = TriggerLifeBars;`, `:509 if (_entryBarsWaiting <= TriggerLifeBars)`, `:910 public int TriggerLifeBars { get; set; }`, `BreakBoxCore.cs:104 public int TriggerLifeBars = 5;`, `:235 if (_st.BreakArmedBars > _cfg.TriggerLifeBars)`, `BoxTests.cs:164 cfg.TriggerLifeBars = 3;`, `:202 "the trigger expires after TriggerLifeBars"`

- [ ] **Step 3: Write minimal implementation**

Fields (`ninjascript/BreakBoxStrategy.cs`, after `_entryBarsWaiting` at line 116):

```csharp
        private int _entryBarsWaiting;

        // §8. The whole parameter surface is seconds; this pair is the only
        // place that knows how many of them a bar is worth. Cached at
        // DataLoaded because the estimate walks the loaded history and
        // BuildConfigs runs again on every panel click.
        private int _barSec = BbScale.FallbackSeconds;
        private string _barSecLabel = "";
```

Default at line 188 — `TriggerLifeBars = 5;` becomes:

```csharp
                TriggerLifeSec = 120;
```

DataLoaded, replacing line 241's bare `BuildConfigs();`:

```csharp
                _barSec = BarSeconds();
                Print("BreakBox: bar ~ " + _barSec + "s (" + _barSecLabel + ")");
                BuildConfigs();
```

The config field loses its bar-flavoured name. `ninjascript/BreakBoxCore.cs:104`:

```csharp
        // A bar count, and the ONLY writer is BuildConfigs' conversion. It is not
        // called *Bars because the surface used to carry the same spelling, and
        // two dials with one name is how a seconds value ends up living in a bar
        // counter without anything complaining.
        public int TriggerLife = 5;
```

and its one reader, `BreakBoxCore.cs:235`:

```csharp
                if (_st.BreakArmedBars > _cfg.TriggerLife)
```

`tests/BoxTests.cs:164` and `:202` follow the rename (`cfg.TriggerLife = 3;` and the
assert message "the trigger expires after TriggerLife").

In `BuildConfigs()`, replace line 290 (`_cfg.TriggerLifeBars = TriggerLifeBars;`) with:

```csharp
            // §8. The conversion lives HERE, not at DataLoaded: a panel toggle
            // rebuilds this config too, and a rebuild that skipped the
            // conversion would hand the engine raw seconds as a bar count.
            _cfg.TriggerLife = BbScale.Bars(TriggerLifeSec, _barSec, 1);
```

`AgeWorkingEntry` at line 509 reads the converted value:

```csharp
            if (_entryBarsWaiting <= _cfg.TriggerLife)
```

The property at lines 908-910:

```csharp
        [NinjaScriptProperty, Range(5, 3600)]
        [Display(Name = "Trigger life (seconds)", Order = 7, GroupName = "03. Engines")]
        public int TriggerLifeSec { get; set; }
```

And `BarSeconds()`, added at the end of the Lifecycle region (before the `#endregion` at line 324):

```csharp
        // How many seconds is one bar of the primary series worth? Time series
        // answer exactly. Tick, volume and range bars are ESTIMATED from the
        // loaded history, because Javier runs 150-tick charts elsewhere in this
        // workspace and a hard throw would turn "que escale sola" into "no
        // carga". The model is calibrated for time bars; the estimate makes a
        // tick chart usable and visibly approximate rather than silently wrong.
        private int BarSeconds()
        {
            int v = BarsPeriod.Value < 1 ? 1 : BarsPeriod.Value;
            switch (BarsPeriod.BarsPeriodType)
            {
                case BarsPeriodType.Second:
                    _barSecLabel = "exact";
                    return v;
                case BarsPeriodType.Minute:
                    _barSecLabel = "exact";
                    return v * 60;
                case BarsPeriodType.Day:
                    _barSecLabel = "exact";
                    return v * 86400;
            }

            int n = Bars != null ? Bars.Count : 0;
            int want = n - 1;
            if (want > 5000)
                want = 5000;                    // one session of 150-tick bars is already plenty
            if (want >= BbScale.MinEstimateSamples)
            {
                double[] gaps = new double[want];
                int first = n - want;
                for (int i = 0; i < want; i++)
                    gaps[i] = (Bars.GetTime(first + i) - Bars.GetTime(first + i - 1)).TotalSeconds;

                int est = BbScale.EstimateBarSeconds(gaps, want);
                if (est > 0)
                {
                    _barSecLabel = "est, " + v + "-" + BarsPeriod.BarsPeriodType;
                    return est;
                }
            }

            _barSecLabel = "fallback";
            Print("BreakBox WARNING: " + BarsPeriod.BarsPeriodType + " series with " + n
                  + " bars loaded — too little history to estimate the bar size. Falling back to "
                  + BbScale.FallbackSeconds + "s, so EVERY seconds-based horizon is now a guess.");
            return BbScale.FallbackSeconds;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -rn 'TriggerLifeBars' ninjascript/ tests/ ; bash scripts/check.sh`
Expected: the grep prints nothing (exit 1 from grep is expected), then `ALL PASS` and `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxStrategy.cs ninjascript/BreakBoxCore.cs tests/BoxTests.cs && \
git commit -m "feat(shell): BarSeconds() and the seconds-to-bars conversion in BuildConfigs" \
  -m "Spec 8. Trigger life is now seconds on the surface and BbConfig.TriggerLife in the config, so the two can never share a spelling again. The conversion sits inside BuildConfigs because every panel toggle rebuilds the config, and a rebuild that skipped it would feed the engine raw seconds as a bar count." \
  -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: `BbGateReport` — the shared block report

**Files:**
- Modify: `ninjascript/BreakBoxTypes.cs` (new sealed class, before `BbScale`)
- Modify: `tests/ShellTests.cs` (add to `Run()`)
- Test: `tests/ShellTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public sealed class BbGateReport` with `string Block`, `string BlockDetail`, `int GateDepth`, `void Set(string, string, int)`, `void Clear()`. Phase 2's `BbCloudState.Gate` and Phase 3's `BbEngineState.Gate` each hold **their own instance** (§4.2) and never share one.

- [ ] **Step 1: Write the failing test**

In `tests/ShellTests.cs`, add `GateReport();` to `Run()` and:

```csharp
    // §4.2. One report per engine, never shared. Its whole job is that the panel
    // can never again print READY next to "(out of band)" — v1 displayed both
    // and connected neither, and Javier watched a dead strategy for an hour.
    private static void GateReport()
    {
        T.Section("Gate report — what blocked, and how deep");

        var g = new BbGateReport();
        T.Check(g.Block == "", "a fresh report blocks nothing");
        T.CheckInt(g.GateDepth, -1, "and has no failing gate");

        g.Set("box range", "range 275.00 = 7.2x ATR (max 6.0)", 2);
        T.Check(g.Block == "box range", "the block is the ladder row that failed");
        T.Check(g.BlockDetail == "range 275.00 = 7.2x ATR (max 6.0)", "the detail is what it needs vs what it has");
        T.CheckInt(g.GateDepth, 2, "the depth dims everything after it");

        // -1, not 0. The panel renders rows BEFORE GateDepth as passed, so a
        // Clear() that left the depth at 0 would dim the whole ladder and report
        // a healthy engine as blocked at its first gate.
        g.Clear();
        T.Check(g.Block == "", "Clear empties the block");
        T.CheckInt(g.GateDepth, -1, "and returns the depth to 'no failing gate'");

        // Engines write these from early returns on the hot path. A null there
        // is a NullReferenceException inside the panel's render, one layer away
        // from where it was caused.
        g.Set(null, null, 0);
        T.Check(g.Block == "" && g.BlockDetail == "", "null is stored as empty, never as null");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: compile failure — `error CS0246: The type or namespace name 'BbGateReport' could not be found`

- [ ] **Step 3: Write minimal implementation**

In `ninjascript/BreakBoxTypes.cs`, immediately before `public static class BbScale`:

```csharp

    // What blocked an engine on THIS bar, in the words the panel prints.
    //
    // One instance per engine and never shared (§4.2): two engines writing one
    // report puts the cloud's ladder under the box's headline, which is a worse
    // failure than no ladder at all — it reads as an explanation.
    public sealed class BbGateReport
    {
        public string Block = "";           // "" when nothing blocks
        public string BlockDetail = "";     // "range 275.00 = 7.2x ATR (max 6.0)"
        public int GateDepth = -1;          // index of the first failing gate; -1 = none

        public void Set(string block, string detail, int depth)
        {
            // Empty, never null: this is written from early returns on the hot
            // path and read on the WPF thread, where a null surfaces as a
            // NullReferenceException one layer away from what caused it.
            Block = block ?? "";
            BlockDetail = detail ?? "";
            GateDepth = depth;
        }

        public void Clear()
        {
            Block = "";
            BlockDetail = "";
            GateDepth = -1;
        }
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: `ALL PASS (N checks)`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxTypes.cs tests/ShellTests.cs && \
git commit -m "feat(types): BbGateReport, one block report per engine" \
  -m "Spec 4.2 and 9.3. Depth -1 means 'nothing failed'; 0 would dim the whole ladder. Cloud and box hold separate instances so neither can overwrite the other's explanation." \
  -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

> **Task 80 (`BbMath.CloseInRange`) belongs HERE**, as a sibling of Task 4 — it was
> written into Phase 5 and moved forward. Its whole claim is "one implementation, so
> the picture and the engine cannot drift", and that claim is unachievable if the
> helper arrives three phases after the cloud engine that needs it: Phase 2 would
> hand-roll the ratio and Phase 5 would add the second copy, which is exactly the
> drift the task exists to prevent. Phase 2's cloud gate (c) calls
> `BbMath.CloseInRange(bar, dir) >= cfg.CloseInRange` from the day it is written.
> The task body travels unchanged from the Phase 5 file; the assembler places it at
> this anchor.

---

### Task 80: `BbMath.CloseInRange` — the one piece of signal-candle maths Vision must not re-derive

**Files:**
- Modify: `projects/Trading/BreakBox/ninjascript/BreakBoxTypes.cs:193` (insert immediately before the `HhmmToSecs` comment block at lines 193-200)
- Create: `projects/Trading/BreakBox/tests/VisionTests.cs`
- Modify: `projects/Trading/BreakBox/tests/Program.cs:48` (the `BracketTests.Run();` line)

**Interfaces:**
- Consumes: `BbBar` (`BreakBoxTypes.cs:23-27`), the `T.Check` / `T.CheckClose` harness (`tests/Program.cs:13-41`).
- Produces: `public static double BbMath.CloseInRange(BbBar bar, int dir)` — the §5.2 step-5 gate (c) ratio, NaN on a zero-range bar. `BreakBoxVision.cs` calls it for its dim-gold classification; the cloud engine's own gate (c) should call it too so the picture and the engine cannot drift.

- [ ] **Step 1: Write the failing test**

Create `tests/VisionTests.cs`:

```csharp
// VisionTests — the pure maths BreakBoxVision paints with.
//
// Vision is an NT8 file and cannot be compiled here, so what this pins is the
// one non-trivial arithmetic it shares with the cloud engine: the signal
// candle's close-in-range ratio. If Vision computed its own version, the chart
// would paint gold bars the engine never saw, which is precisely the "picture
// that lies" §12 exists to prevent.
using System;
using BreakBoxCore;

public static class VisionTests
{
    public static void Run()
    {
        CloseInRangeRatio();
    }

    private static BbBar Bar(double o, double h, double l, double c)
    {
        return new BbBar { Time = new DateTime(2026, 8, 16, 12, 0, 0), Open = o, High = h, Low = l, Close = c, Volume = 100 };
    }

    private static void CloseInRangeRatio()
    {
        T.Section("Vision — signal candle close-in-range");

        // Both reference signal candles were WICKLESS: the close sat exactly on
        // the extreme, ratio 1.00 (spec §2).
        T.CheckClose(BbMath.CloseInRange(Bar(100.0, 105.0, 100.0, 105.0), +1), 1.00, "wickless up bar reads 1.00 long");
        T.CheckClose(BbMath.CloseInRange(Bar(100.0, 105.0, 100.0, 105.0), -1), 0.00, "the same bar reads 0.00 short");
        T.CheckClose(BbMath.CloseInRange(Bar(105.0, 105.0, 100.0, 100.0), -1), 1.00, "wickless down bar reads 1.00 short");

        // The panel mock's blocked case: body 0.42 against a 0.60 gate.
        T.CheckClose(BbMath.CloseInRange(Bar(91.0, 100.0, 90.0, 94.2), +1), 0.42, "mid-range close reads 0.42");

        // A zero-range bar has no ratio. NaN, not 1.0: every comparison against
        // NaN is false, so a halted-tape doji FAILS the gate instead of becoming
        // the best-looking signal candle of the session.
        double flat = BbMath.CloseInRange(Bar(100.0, 100.0, 100.0, 100.0), +1);
        T.Check(double.IsNaN(flat), "a zero-range bar has no ratio");
        T.Check(!(flat >= 0.60), "and it fails the gate closed");
    }
}
```

Wire it into the runner:

```csharp
        BoxTests.Run();
        BracketTests.Run();
        VisionTests.Run();
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error|FAIL|ALL PASS"`
Expected: build error — `error CS0117: 'BbMath' does not contain a definition for 'CloseInRange'`

- [ ] **Step 3: Write minimal implementation**

In `ninjascript/BreakBoxTypes.cs`, insert before the `// ET seconds-of-day from an HHMM integer.` comment (line 193):

```csharp
        // Where in its own range did this bar CLOSE, seen from `dir`? 1.00 is a
        // wickless close on the extreme — which is what BOTH reference signal
        // candles read (spec §2) — and 0.00 closes on the wrong end.
        //
        // NaN on a zero-range bar, deliberately. Every comparison against NaN is
        // false, so a flat bar FAILS the gate instead of passing it on a 0/0;
        // the alternative is that a halted tape prints the best-looking signal
        // candle of the session.
        public static double CloseInRange(BbBar bar, int dir)
        {
            double range = bar.High - bar.Low;
            if (range <= 0.0)
                return double.NaN;
            return dir > 0 ? (bar.Close - bar.Low) / range : (bar.High - bar.Close) / range;
        }

```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: `ALL PASS (<n> checks)`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxTypes.cs tests/VisionTests.cs tests/Program.cs && git commit -m "feat(core): BbMath.CloseInRange — the shared signal-candle ratio, NaN on a flat bar

Vision and the cloud engine both need gate (c) from spec §5.2 step 5. One
implementation so the painted bar and the traded bar cannot disagree.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

---

### Task 5: B2 — pass `canTrade` INTO the engine

**Files:**
- Modify: `ninjascript/BreakBoxCore.cs:196-264` (the `OnBar` signature and the fire gate at `:247`)
- Modify: `ninjascript/BreakBoxStrategy.cs:385-394`
- Modify: `tests/BoxTests.cs` (every `OnBar` call site, plus one new case)
- Test: `tests/BoxTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `BbEngine.OnBar(BbBar bar, int secs, DateTime sessionDate, double atr, bool atrWarm, bool canTrade, bool positioned)` — the shared-contract signature. `canTrade` suppresses **arming and firing only**; box accumulation, the excursion tracker and the trigger clock run unconditionally (§5.2 step 1b). This is the **only** task that changes this signature or the `:385-394` call site — Phase 3's box rewrite touches `BreakBoxCore.cs` alone and leaves the shell's call as it finds it.
- **Coverage, honestly:** `CanTradeGatesArmingOnly` lives in `tests/BoxTests.cs`, which §6.4's box rewrite replaces **wholesale**. This assert therefore has a shelf life of two phases. It is still worth writing — it is what makes this task's red-then-green real — but B2's permanent coverage is re-established by Phase 3's arming tests, not by this file surviving.

- [ ] **Step 1: Write the failing test**

First migrate the existing call sites (mechanical, `canTrade` slots in before `positioned`):

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
sed -i -E 's/, (atr|[0-9]+(\.[0-9]+)?), true, false\)/, \1, true, true, false)/g' tests/BoxTests.cs && \
! grep -q ', true, false)' tests/BoxTests.cs && \
grep -c 'true, true, false)' tests/BoxTests.cs
```

Then add `CanTradeGatesArmingOnly();` to `BoxTests.Run()` and the case:

```csharp
    private static void CanTradeGatesArmingOnly()
    {
        T.Section("canTrade — suppresses arming, never the bookkeeping (spec 5.2 step 1b)");

        var cfg = Cfg();
        cfg.BoxSource = BbBoxSource.PriorPeriod;
        cfg.HtfMinutes = 60;
        cfg.EnableBreak = true;
        cfg.RequireCloseOutside = true;

        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Feed(eng, Open, 60, 110.0, 100.0, 4.0);

        // AUTO-TRADE off, or locked out, or warming up. The bar breaks the box
        // and the engine must not arm anything.
        DateTime t = Open.AddMinutes(60);
        var a = eng.OnBar(Bar(t, 109, 112, 108.5, 111.0), Secs(t), t.Date, 4.0, true, false, false);
        T.Check(!a.Fire, "a break with canTrade=false does not fire");
        T.Check(!eng.BreakArmed, "and does not arm");
        T.CheckInt(st.BreakSpentDir, 0, "and does not spend the edge");

        // The box built while blocked is the SAME box. v1 computed canTrade
        // after OnBar and discarded it (§11 B2), so the engine armed and spent
        // latches while flat — ten minutes with the switch off left a hole in
        // the state and re-enabling resumed from a burnt edge.
        int boxId = eng.Box.Id;
        t = t.AddMinutes(1);
        a = eng.OnBar(Bar(t, 111, 113, 110.5, 112.0), Secs(t), t.Date, 4.0, true, true, false);
        T.CheckInt(eng.Box.Id, boxId, "the box survived the blocked bar");
        T.Check(a.Fire, "and the edge is still there to trade on re-enable");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: compile failure — `error CS1501: No overload for method 'OnBar' takes 7 arguments`

- [ ] **Step 3: Write minimal implementation**

`ninjascript/BreakBoxCore.cs` — the signature at line 201 and its doc comment above it:

```csharp
        // Feed one CLOSED bar. `secs` is its ET seconds-of-day, `sessionDate`
        // the trading day it belongs to (used only for the daily trade counter),
        // `atr` the warm house ATR, `canTrade` whether the shell would accept an
        // entry at all (auto-trade on, not locked out, indicators warm), and
        // `positioned` whether it already holds one.
        //
        // canTrade suppresses ARMING and FIRING and nothing else (§5.2 step 1b):
        // the box, the excursion and the trigger clock below it run on every
        // closed bar regardless, or a flat period leaves a hole in the state and
        // re-enabling resumes from a stale box.
        public BbAction OnBar(BbBar bar, int secs, DateTime sessionDate, double atr, bool atrWarm,
                              bool canTrade, bool positioned)
```

The fire gate at line 247:

```csharp
            if (!canTrade || positioned || !windowOpen || !budget)
                return a;
```

`ninjascript/BreakBoxStrategy.cs` lines 385-394 — `canTrade` is already computed at :385; stop discarding it:

```csharp
            bool canTrade = _uiAutoTrade && !_lockout && _atr.IsWarm && _e50.IsWarm;
            var a = _engine.OnBar(bar, secs, sessionDate, _atr.Value, _atr.IsWarm, canTrade,
                                  _inTrade || _entryPending);

            if (ShowBox) DrawBox();

            // canTrade is no longer re-tested here — the engine owns that
            // decision now. The position checks stay: SubmitEntry while
            // positioned is the one mistake that costs real money, and it is
            // cheap to refuse twice.
            if (a.Fire && !_inTrade && !_entryPending)
                SubmitEntry(a);
            else if (_entryPending)
                AgeWorkingEntry();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS" && bash scripts/check.sh`
Expected: `ALL PASS (N checks)` and `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxCore.cs ninjascript/BreakBoxStrategy.cs tests/BoxTests.cs && \
git commit -m "fix(engine): pass canTrade into OnBar instead of discarding it (B2)" \
  -m "It was computed at Strategy.cs:385 and thrown away at :391, after the engine had already armed and spent latches during warmup and lockout. It now gates arming and firing only; box, excursion and trigger clock still run every bar." \
  -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: B6 — one trigger clock, owned by the engine, plus the refusal callbacks

**Files:**
- Modify: `ninjascript/BreakBoxCore.cs:144-180` (`BbEngineState`), `:266-276` (after `OnEntryFilled`), `:432-455` (`DisarmBreak` and the public accessors)
- Modify: `tests/BoxTests.cs` (add to `Run()`)
- Test: `tests/BoxTests.cs`

**Interfaces:**
- Consumes: `BbEngine.BreakArmed` (existing, `:453`).
- Produces: `BbEngineState.DisarmReason`, `BbEngine.LastDisarmReason { get; }`, `BbEngine.OnEntryRejected(string reason)`, `BbEngine.OnTriggerExpired()`. The shell mirrors `BreakArmed` — it never runs a competing clock. **`LastDisarmReason` is load-bearing downstream**: Task 7's `AgeWorkingEntry` logs `"engine:" + _engine.LastDisarmReason`, so the box rewrite must carry it forward even as it rewrites everything around it.
- **Coverage, honestly:** as in Task 5 — `TriggerClockAndRefusals` lives in `tests/BoxTests.cs` and §6.4 replaces that file wholesale. B6's permanent coverage is re-established by Phase 3's expiry/refusal tests; this assert exists to make *this* task's red-then-green real.

- [ ] **Step 1: Write the failing test**

Add `TriggerClockAndRefusals();` to `BoxTests.Run()` and:

```csharp
    private static void TriggerClockAndRefusals()
    {
        T.Section("Trigger — one clock in the engine, and what a refusal hands back");

        var cfg = Cfg();
        cfg.BoxSource = BbBoxSource.PriorPeriod;
        cfg.HtfMinutes = 60;
        cfg.EnableBreak = true;
        cfg.RequireCloseOutside = true;
        cfg.TriggerLife = 3;            // bars; Task 3 renamed it off the surface's spelling

        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Feed(eng, Open, 60, 110.0, 100.0, 4.0);

        DateTime t = Open.AddMinutes(60);
        eng.OnBar(Bar(t, 109, 112, 108.5, 111.0), Secs(t), t.Date, 4.0, true, true, false);
        T.Check(eng.BreakArmed, "armed");

        for (int i = 1; i <= 4; i++)
        {
            DateTime v = t.AddMinutes(i);
            eng.OnBar(Bar(v, 111, 112.5, 110.5, 111.5), Secs(v), v.Date, 4.0, true, true, false);
        }

        // The shell reads exactly these two to decide whether its resting order
        // is still wanted (§11 B6). v1 counted its own bars from SUBMIT while
        // the engine counted from ARM and neither cancelled the other's object,
        // so an inside-close disarm left a stop entry resting on a dead level.
        T.Check(!eng.BreakArmed, "the engine's own clock expired the trigger");
        T.Check(eng.LastDisarmReason == "expired", "and says why, so the shell can log the cancel");

        // Idempotent: the shell acknowledges without asking whether it needs to.
        eng.OnTriggerExpired();
        T.Check(!eng.BreakArmed, "acknowledging an already-expired trigger changes nothing");

        // A REFUSAL is not an expiry. The trade was never attempted, so the edge
        // comes back and its next break may arm again.
        var st2 = new BbEngineState();
        var eng2 = new BbEngine(cfg, st2);
        Feed(eng2, Open, 60, 110.0, 100.0, 4.0);
        DateTime u = Open.AddMinutes(60);
        var a = eng2.OnBar(Bar(u, 109, 112, 108.5, 111.0), Secs(u), u.Date, 4.0, true, true, false);
        T.Check(a.Fire, "armed a trigger the shell is about to refuse");

        eng2.OnEntryRejected("qty<1");
        T.Check(!eng2.BreakArmed, "the refusal disarms");
        T.CheckInt(st2.BreakSpentDir, 0, "and hands the edge back");

        u = u.AddMinutes(1);
        a = eng2.OnBar(Bar(u, 111, 113, 110.5, 112.0), Secs(u), u.Date, 4.0, true, true, false);
        T.Check(a.Fire, "so the next break of that edge can still be taken");
        T.CheckInt(st2.TradesThisBox, 0, "and no budget was spent by a trade nobody took");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: compile failure — `error CS1061: 'BbEngine' does not contain a definition for 'LastDisarmReason'`

- [ ] **Step 3: Write minimal implementation**

`ninjascript/BreakBoxCore.cs` — in `BbEngineState`, after `BreakSpentDir` (line 166):

```csharp
        public int BreakSpentDir;
        // Why the last disarm happened. The shell mirrors the engine's clock
        // (§11 B6) and logs this when it cancels the order that went with it —
        // "the order vanished" with no reason is how a live session becomes
        // unauditable after the fact.
        public string DisarmReason = "";
```

`DisarmBreak` at lines 432-438 — the `why` argument stops being decoration:

```csharp
        private void DisarmBreak(string why)
        {
            _st.BreakArmed = false;
            _st.BreakDir = 0;
            _st.BreakTriggerPx = 0.0;
            _st.BreakArmedBars = 0;
            _st.DisarmReason = why;
        }
```

Public accessors, next to the existing ones at lines 453-455:

```csharp
        public bool BreakArmed { get { return _st.BreakArmed; } }
        public double BreakTriggerPx { get { return _st.BreakTriggerPx; } }
        public int BreakDir { get { return _st.BreakDir; } }
        public string LastDisarmReason { get { return _st.DisarmReason; } }
```

And the two callbacks, after `OnEntryFilled` (line 276):

```csharp

        // The shell refused, cancelled or lost the entry this engine armed. The
        // trade was never taken, so the edge is handed back: v1 marked it spent
        // at ARM time (§11 B3), which turns one refused order into a whole
        // session with that edge silently dead.
        public void OnEntryRejected(string reason)
        {
            DisarmBreak("refused:" + reason);
            _st.BreakSpentDir = 0;
        }

        // The trigger ran out its own clock and the shell has now cancelled the
        // order that went with it. There is nothing to undo — the clock lives
        // here and DisarmBreak already ran on the bar that expired it — so this
        // only acknowledges, and stays idempotent because the shell calls it for
        // the owning engine without asking first.
        //
        // It deliberately does NOT hand the edge back the way a refusal does: an
        // expiry means price sat outside that edge for the whole trigger life
        // without filling, and re-arming there on the next bar is exactly the
        // every-bar re-arm loop the spent latch exists to stop. Expiry keeps the
        // edge spent, and still does after §6.2 — what changes there is the SHAPE
        // of the bound, not the verdict: the single latch becomes `BoxArmsPerEdge`
        // attempts. That is a budget, not a refund.
        public void OnTriggerExpired()
        {
            if (_st.BreakArmed)
                DisarmBreak("expired");
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: `ALL PASS (N checks)`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxCore.cs tests/BoxTests.cs && \
git commit -m "feat(engine): engine-owned trigger clock plus OnEntryRejected/OnTriggerExpired (B6)" \
  -m "The engine already counted from ARM; it now reports the disarm reason so the shell can mirror it instead of running a second clock from SUBMIT. A refusal restores the edge, an expiry does not — and the box rewrite does not change that verdict, it only replaces the single latch with an arms-per-edge budget." \
  -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: B4 + B7 — `_owningEngine`, the three refusal paths, and the shell mirror

**Files:**
- Modify: `ninjascript/BreakBoxCore.cs:36-39` (the `BbEntryEngine` enum gains `Cloud`)
- Modify: `ninjascript/BreakBoxStrategy.cs:106-116` (fields), `:391-394`, `:457-461`, `:506-524`, `:733-734`
- Modify: `ninjascript/BreakBoxPanel.cs:337-361` (`PanelManualEntry` claims no token)
- Test: `scripts/check.sh` + a grep gate (order plumbing: `CancelOrder`, `OrderState` and `Print` are NT8-bound; the engine half is asserted in Task 6)

**Interfaces:**
- Consumes: `BbEngine.BreakArmed`, `BbEngine.LastDisarmReason`, `BbEngine.OnEntryRejected(string)`, `BbEngine.OnTriggerExpired()`.
- Produces: `enum BbEntryEngine { Break = 0, Retrace = 1, Cloud = 2 }` — the contract enum, in full, three phases before the cloud engine that uses it. Also `private BbEntryEngine _owningEngine;`, `private bool _entryFromEngine;`, `private void OnEntryRejected(BbEntryEngine engine, string reason);`, `private void MirrorEngineDisarm();`, `private void CancelWorkingEntry(string why, bool engineDisarmed);`.
- **This task is the sole declaration site of `_owningEngine` and `_entryFromEngine`.** Phase 2 and Phase 3 only *assign* them — Phase 2 routes cloud refusals through the same `OnEntryRejected` by setting `_owningEngine = BbEntryEngine.Cloud` on submit, and re-declaring the field there is a `CS0102`, not a merge.

- [ ] **Step 1: Write the failing test**

The gate — every refusal path must reach an engine:

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
grep -nE 'OnEntryRejected\(|MirrorEngineDisarm\(|_owningEngine' ninjascript/BreakBoxStrategy.cs
```

- [ ] **Step 2: Run test to verify it fails**

Run: the command above
Expected: no output at all — none of the three refusal paths (`qty < 1` at `:460`, `CancelWorkingEntry` at `:514`, the `OrderState.Rejected` entry branch at `:733`) tells any engine anything

- [ ] **Step 2b: Give the enum its third value first**

Everything below routes on `BbEntryEngine.Cloud`, and today the enum stops at
`Retrace`. `ninjascript/BreakBoxCore.cs:36-39` becomes the contract enum in full:

```csharp
    public enum BbEntryEngine
    {
        Break = 0,              // trade the break of the edge
        Retrace = 1,            // let it extend, then trade the return to the edge
        Cloud = 2               // §5.4's ribbon/gold-candle engine — no code yet, but
                                // the shell has to be able to NAME the owner of an
                                // order before the engine that arms it exists, or
                                // every routing branch below is written against a
                                // value that will not compile.
    }
```

The value is added here and not with the cloud engine on purpose: the ownership
plumbing is what stops a cloud refusal from cancelling the box's trigger, and it has
to be right on the day the second engine lands, not the day after.

- [ ] **Step 3: Write minimal implementation**

Fields, after `_pendingAction` (line 111):

```csharp
        private BbAction _pendingAction;
        // §4.1. Which engine armed the trigger this order came from. Expiry,
        // disarm and rejection are forwarded ONLY to it, so a cloud refusal can
        // never cancel the box's trigger once both engines are live.
        private BbEntryEngine _owningEngine;
        // A hand-clicked entry owns no token: nothing armed it, so nothing may
        // be handed back to an engine when it is cancelled.
        private bool _entryFromEngine;
```

`SubmitEntry` — the `qty < 1` path at lines 459-461, and the ownership stamp (written BEFORE the submit, like every other in-flight flag):

```csharp
            int qty = SizedQty();
            if (qty < 1)
            {
                // B4. The engine armed this trigger; the shell is throwing it
                // away. Say so, or the edge stays spent on a trade that was
                // never attempted.
                OnEntryRejected(a.Engine, "qty<1");
                return;
            }
```

and, in the same method, alongside `_entryPending = true;` (line 478):

```csharp
            _entryPending = true;
            _owningEngine = a.Engine;
            _entryFromEngine = true;
            _dir = a.Dir;
```

Replace `AgeWorkingEntry` (lines 506-512) with the mirror:

```csharp
        // B6/B7 — ONE clock, and it belongs to the engine. The shell mirrors:
        // when the engine that armed this trigger disarms it (its bar budget ran
        // out, or price closed back inside), the resting order that trigger
        // produced is cancelled here. v1 counted its own bars from SUBMIT while
        // the engine counted from ARM, and neither cancelled the other's object
        // — so an inside-close disarm left a stop entry resting on a dead level
        // for the rest of the session.
        private void AgeWorkingEntry()
        {
            _entryBarsWaiting++;                // display only; nothing decides on it
            if (!_entryFromEngine || _engine == null || _owningEngine == BbEntryEngine.Cloud)
                return;
            if (_engine.BreakArmed)
                return;
            CancelWorkingEntry("engine:" + _engine.LastDisarmReason, true);
        }
```

`CancelWorkingEntry` (lines 514-524) gains the overload:

```csharp
        private void CancelWorkingEntry(string why)
        {
            CancelWorkingEntry(why, false);
        }

        // `engineDisarmed` = the engine already killed the trigger and this is
        // the shell catching up. Everything else is a REFUSAL: the trigger was
        // still good and the shell threw the trade away, which is the case that
        // has to hand the edge back (§11 B4).
        private void CancelWorkingEntry(string why, bool engineDisarmed)
        {
            if (!_entryPending)
                return;
            _entryPending = false;
            _entryBarsWaiting = 0;
            if (_entryOrder != null && (_entryOrder.OrderState == OrderState.Working
                                        || _entryOrder.OrderState == OrderState.Accepted))
                CancelOrder(_entryOrder);
            _entryOrder = null;

            if (!_entryFromEngine)
                return;
            _entryFromEngine = false;
            if (engineDisarmed)
            {
                if (_owningEngine != BbEntryEngine.Cloud && _engine != null)
                    _engine.OnTriggerExpired();
            }
            else
            {
                OnEntryRejected(_owningEngine, why);
            }
        }

        // The single refusal callback. It routes on the OWNING engine (§4.1):
        // only the box engine exists today, and BreakBoxCloud claims
        // BbEntryEngine.Cloud here the moment it lands.
        private void OnEntryRejected(BbEntryEngine engine, string reason)
        {
            Print("BreakBox: entry refused (" + reason + ")");
            if (engine != BbEntryEngine.Cloud && _engine != null)
                _engine.OnEntryRejected(reason);
        }
```

The third reachable path — `OnOrderUpdate`'s rejected-entry branch at lines 733-734:

```csharp
                else if (sig == SigLong || sig == SigShort)
                {
                    _entryPending = false;
                    if (_entryFromEngine)
                    {
                        _entryFromEngine = false;
                        OnEntryRejected(_owningEngine, "order_rejected");
                    }
                }
```

And the fill path at line 669-671 clears the ownership flag, because a filled entry consumed its token legitimately:

```csharp
                _entryPending = false;
                _entryFromEngine = false;
                _entryBarsWaiting = 0;
```

In `ninjascript/BreakBoxPanel.cs`, `PanelManualEntry` — alongside `_entryPending = true;` (line 356):

```csharp
            _entryPending = true;
            _owningEngine = a.Engine;
            _entryFromEngine = false;       // nothing armed this; there is no token to hand back
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -cE 'OnEntryRejected\(' ninjascript/BreakBoxStrategy.cs && bash scripts/check.sh`
Expected: `4` (the three call sites plus the definition), then `ALL PASS` and `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxCore.cs ninjascript/BreakBoxStrategy.cs ninjascript/BreakBoxPanel.cs && \
git commit -m "fix(shell): route every refusal to the owning engine, mirror its disarm (B4, B7)" \
  -m "One OnEntryRejected called from qty<1, CancelWorkingEntry and the rejected-order branch; _owningEngine so a refusal never disarms the other engine's trigger; the resting order is now cancelled when the engine disarms, including on an inside close. BbEntryEngine gains Cloud now rather than with the cloud engine, because ownership routing has to be correct on the day the second engine lands." \
  -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: B14 — delete the unreachable degenerate-stop guard

**Files:**
- Modify: `ninjascript/BreakBoxStrategy.cs:463-473` (delete)
- Modify: `tests/BracketTests.cs` (add to `Run()`)
- Test: `tests/BracketTests.cs`

**Interfaces:**
- Consumes: `BbExits.SeedStop`.
- Produces: nothing. `SubmitEntry` no longer computes a probe stop, so `StopInputs(a)` is called once per trade, at the fill.

- [ ] **Step 1: Write the failing test**

Add `DegenerateStopIsImpossible();` to `BracketTests.Run()` and:

```csharp
    // §11 B14. The shell refused any entry whose seeded stop landed within one
    // tick of the trigger. That cannot happen: SeedStop floors the DISTANCE at
    // one tick (BreakBoxExits.cs:188) after the fallback and after both clamps.
    // The guard was dead code that read like a live safety net, which is the
    // worst kind — it answers "what protects us here?" with a lie.
    private static void DegenerateStopIsImpossible()
    {
        T.Section("Stop — the degenerate-stop refusal is unreachable");

        var cfg = Cfg();
        cfg.StopSource = BbStopSource.Candle;
        cfg.StopBufferTicks = 0;
        cfg.ManualStopTicks = 40;
        cfg.StopMinAtr = 0.0;
        cfg.StopMaxAtr = 1e9;
        string why;
        var inp = NoStructure();

        // The exact case the guard named: a wickless signal bar, so the
        // structural stop IS the entry price.
        inp.SignalBarLow = 101.00;
        double s = BbExits.SeedStop(cfg, +1, 101.00, 0.0, false, inp, out why);
        T.Check(Math.Abs(101.00 - s) >= 0.25, "a stop AT the entry never survives SeedStop");
        T.Check(why == "manual_fallback", "a same-side structure is not usable at all, so it falls back");

        // The other way to ask for a zero-width stop: collapse the ATR band onto
        // zero and let the cap do it.
        cfg.StopMinAtr = 0.0;
        cfg.StopMaxAtr = 0.0;
        inp.SignalBarLow = 100.75;
        s = BbExits.SeedStop(cfg, +1, 101.00, 4.00, true, inp, out why);
        T.Check(Math.Abs(101.00 - s) >= 0.25, "a zero-width band still cannot produce a zero-width stop");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: compile failure — `error CS0103: The name 'DegenerateStopIsImpossible' does not exist in the current context` (the harness compiles `Run()` before the method exists; write both, then this step confirms the asserts themselves pass on the untouched `SeedStop`, proving the guard is dead before deleting it)

- [ ] **Step 3: Write minimal implementation**

Delete `ninjascript/BreakBoxStrategy.cs` lines 463-473 in full — the comment, the `StopInputs`/`SeedStop` probe that exists only to feed the guard, and the guard. `SubmitEntry` becomes:

```csharp
        private void SubmitEntry(BbAction a)
        {
            int qty = SizedQty();
            if (qty < 1)
            {
                OnEntryRejected(a.Engine, "qty<1");
                return;
            }

            string sig = a.Dir > 0 ? SigLong : SigShort;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS" && grep -c 'degenerate stop' ninjascript/BreakBoxStrategy.cs`
Expected: `ALL PASS (N checks)`, then `0` from the grep

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxStrategy.cs tests/BracketTests.cs && \
git commit -m "fix(shell): delete the unreachable degenerate-stop refusal (B14)" \
  -m "SeedStop floors the stop distance at one tick after the fallback and both clamps, so |trigger - stop| >= TickSize always. An assert pins that property; the guard and its probe are gone, and B4's callback is deliberately NOT wired to them." \
  -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: B8, B9, B10 — the entry window, the daily budget and the loss limit

**Files:**
- Modify: `ninjascript/BreakBoxCore.cs:122-126` (the pure defaults)
- Modify: `ninjascript/BreakBoxStrategy.cs:219-224` (the NinjaScript defaults)
- Modify: `tests/ShellTests.cs` (add to `Run()`)
- Test: `tests/ShellTests.cs`

**Interfaces:**
- Consumes: `BbMath.InWindow`, `BbMath.HhmmToSecs`.
- Produces: `BbConfig.EntryWindowStartHhmm = 930`, `BbConfig.EntryWindowEndHhmm = 1545`, `BbConfig.MaxTradesPerDay = 30`. Phase 3's rewritten `BbConfig` keeps all three.
- **Not in this phase:** B11 (`SlotOf` off-by-one, `BreakBoxCore.cs:291-323`) and B12 (`BreakSpentDir` as a single int, `BreakBoxCore.cs:166`) are closed by **deletion** in Phase 3 (§6.4). Do not patch them here.

- [ ] **Step 1: Write the failing test**

Add `EntryWindowAndBudget();` to `ShellTests.Run()` and:

```csharp
    private static void EntryWindowAndBudget()
    {
        T.Section("Session — the entry window and the daily budget (B8, B9)");

        var cfg = new BbConfig();
        T.CheckInt(cfg.EntryWindowStartHhmm, 930, "the window opens at the cash open");
        T.CheckInt(cfg.EntryWindowEndHhmm, 1545, "and shuts 15 minutes before the cash close");

        int lo = BbMath.HhmmToSecs(cfg.EntryWindowStartHhmm);
        int hi = BbMath.HhmmToSecs(cfg.EntryWindowEndHhmm);
        T.Check(!BbMath.InWindow(BbMath.HhmmToSecs(929) + 59, lo, hi), "09:29:59 is out");
        T.Check(BbMath.InWindow(BbMath.HhmmToSecs(930), lo, hi), "09:30:00 is in");
        T.Check(BbMath.InWindow(BbMath.HhmmToSecs(1544) + 59, lo, hi), "15:44:59 is in");
        T.Check(!BbMath.InWindow(BbMath.HhmmToSecs(1545), lo, hi), "15:45:00 is out");

        // The old default was 18:00 -> 16:00: a 22-hour window that gates
        // nothing. A no-op gate is worse than no gate at all, because it reads
        // like a decision somebody made.
        T.Check(!BbMath.InWindow(BbMath.HhmmToSecs(300), lo, hi), "03:00 overnight is out");

        // B9. The budget is a governor of LAST resort. 5 a day is a swing
        // number, and the design frequency is 8-12 fills per session (§13 step
        // 4) — the old cap would have silenced the strategy before lunch and
        // called it risk management. DailyLossLimit governs HOW MUCH; this only
        // stops a runaway loop.
        T.CheckInt(cfg.MaxTradesPerDay, 30, "the daily cap sits above the design frequency");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: `FAIL the window opens at the cash open (1800 vs 930)` (and four more)

- [ ] **Step 3: Write minimal implementation**

`ninjascript/BreakBoxCore.cs`, lines 122-126:

```csharp
        // --- Gating
        public int MaxTradesPerBox = 1;
        // A governor of last resort, not a plan. The design frequency is 8-12
        // fills per session (§13 step 4); the daily budget only has to stop a
        // runaway loop. DailyLossLimit in the shell is the real governor,
        // because it measures HOW MUCH, not HOW MANY.
        public int MaxTradesPerDay = 30;
        // RTH only, and off the tape 15 minutes before the cash close. The
        // 18:00 -> 17:00 default it replaces was the whole ETH session: a
        // 22-hour window gates nothing while looking like it does.
        public int EntryWindowStartHhmm = 930;
        public int EntryWindowEndHhmm = 1545;
        public int AtrPeriod = 14;
```

`ninjascript/BreakBoxStrategy.cs`, lines 219-224:

```csharp
                EntryWindowStartHhmm = 930;
                EntryWindowEndHhmm = 1545;
                FlattenHhmm = 1655;
                MaxTradesPerBox = 1;
                MaxTradesPerDay = 30;
                // ON. §7.1 makes this load-bearing: with TP1 at 0.50R taking
                // half off and breakeven armed, a runner can still round-trip
                // from +1R to a full -1R, and after TP1 the open loss is
                // unbounded until the stop. This is what catches the day where
                // that happens three times.
                DailyLossLimit = 450;           // currency
                DailyProfitTarget = 0;          // 0 = off, currency
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS" && grep -n 'DailyLossLimit = ' ninjascript/BreakBoxStrategy.cs`
Expected: `ALL PASS (N checks)` and `DailyLossLimit = 450;`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxCore.cs ninjascript/BreakBoxStrategy.cs tests/ShellTests.cs && \
git commit -m "fix(session): RTH entry window, a real daily cap, loss limit ON (B8, B9, B10)" \
  -m "09:30-15:45 ET replaces a 22-hour no-op window; the daily cap moves from a swing number to 30 so it cannot silence the design frequency; DailyLossLimit becomes the governor that actually measures risk." \
  -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: B13 — warm up only what the active config reads

**Files:**
- Modify: `ninjascript/BreakBoxExits.cs:191` (new method after `SeedStop`)
- Modify: `ninjascript/BreakBoxStrategy.cs:385`
- Modify: `tests/BracketTests.cs` (add to `Run()`)
- Test: `tests/BracketTests.cs`

**Interfaces:**
- Consumes: `BbExitConfig.StopSource`.
- Produces: `public static bool BbExits.StopSourceWarm(BbExitConfig cfg, bool maWarm, bool e50Warm)`. Phase 2's cloud warm-up gate (§5.2 step 1) composes with it rather than replacing it.

- [ ] **Step 1: Write the failing test**

Add `WarmupGatesOnlyWhatIsUsed();` to `BracketTests.Run()` and:

```csharp
    // §11 B13. v1 gated EVERY trade on the EMA(50) being warm, whichever stop
    // source was selected. On 30s bars that is 25 minutes of every session paid
    // to a series `Candle` never looks at — and the panel said WARMING without
    // ever saying what for.
    private static void WarmupGatesOnlyWhatIsUsed()
    {
        T.Section("Warmup — gate only the indicators the active config reads");

        var cfg = Cfg();

        cfg.StopSource = BbStopSource.Candle;
        T.Check(BbExits.StopSourceWarm(cfg, false, false), "Candle reads no average, so it never waits");

        cfg.StopSource = BbStopSource.Manual;
        T.Check(BbExits.StopSourceWarm(cfg, false, false), "nor does Manual");

        // Swing is structural too: when no pivot has been revealed yet SeedStop
        // falls back and REPORTS manual_fallback, which is a better answer than
        // refusing to trade for an unbounded number of bars.
        cfg.StopSource = BbStopSource.Swing;
        T.Check(BbExits.StopSourceWarm(cfg, false, false), "Swing reports its fallback instead of blocking");

        cfg.StopSource = BbStopSource.Ema50;
        T.Check(!BbExits.StopSourceWarm(cfg, true, false), "E50 waits for the E50");
        T.Check(BbExits.StopSourceWarm(cfg, false, true), "and for nothing else");

        // Ma means "far ribbon edge" once MaPeriod = RibbonSlow (§5.1), so it is
        // the one the cloud engine will actually lean on.
        cfg.StopSource = BbStopSource.Ma;
        T.Check(!BbExits.StopSourceWarm(cfg, false, true), "Ma waits for the MA");
        T.Check(BbExits.StopSourceWarm(cfg, true, false), "and for nothing else");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: compile failure — `error CS0117: 'BbExits' does not contain a definition for 'StopSourceWarm'`

- [ ] **Step 3: Write minimal implementation**

`ninjascript/BreakBoxExits.cs`, after `SeedStop` closes (line 191):

```csharp

        // Which moving averages does the SELECTED stop source actually read?
        // Gating every trade on the EMA(50) costs 50 bars of every session to a
        // series `Candle` never touches (§11 B13). Candle, Swing and Manual
        // return true because none of them reads an average: a missing swing
        // falls back and SAYS it fell back, which beats blocking for an
        // unbounded number of bars waiting for a pivot that may not come.
        public static bool StopSourceWarm(BbExitConfig cfg, bool maWarm, bool e50Warm)
        {
            switch (cfg.StopSource)
            {
                case BbStopSource.Ma:    return maWarm;
                case BbStopSource.Ema50: return e50Warm;
                default:                 return true;
            }
        }
```

`ninjascript/BreakBoxStrategy.cs` line 385:

```csharp
            bool canTrade = _uiAutoTrade && !_lockout && _atr.IsWarm
                            && BbExits.StopSourceWarm(_exitCfg, _ma.IsWarm, _e50.IsWarm);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS" && bash scripts/check.sh`
Expected: `ALL PASS (N checks)` and `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxExits.cs ninjascript/BreakBoxStrategy.cs tests/BracketTests.cs && \
git commit -m "fix(shell): warm-up gates only the indicators the active config uses (B13)" \
  -m "The EMA(50) warmup blocked every trade regardless of stop source — 25 minutes of every 30s session paid to a series Candle never reads." \
  -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```


---

### (no Task 11 — B5 moved to Phase 4)

B5 (panel toggles calling `BuildConfigs()` straight off the WPF thread) is no longer
fixed here. Phase 4 rewrites `BreakBoxPanel.cs` end to end and reintroduces the same
bridge under the name `Rebuild()`; patching the five v1 handlers first would mean
writing the fix twice and gating it on `_retraceBtn`, a control Phase 3 deletes. The
task number is left empty on purpose — the other four files reference these numbers.

**Moved to Phase 4 (T67):** the corrected `BreakBoxPanel.cs:16-18` header comment —
buttons that only flip a field do not need the bridge, anything that calls
`BuildConfigs()`/`Rebuild()` does, because that method REPLACES `_cfg` and `_engine`
and doing it from the WPF thread swaps the engine out from under a running
`OnBarUpdate`. That is §11 B5, and it shows up as one impossible bar, once, weeks
later.
## Phase 2 — Cloud engine

### Task 20: `BbCloudConfig` + `BbCloudState` + the `BbCloud` skeleton (step 0 and step 1)

**Files:**
- Create `/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/ninjascript/BreakBoxCloud.cs` (new, pure — zero `using NinjaTrader.*`, `namespace BreakBoxCore`, C# 7.3)
- Create `/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/tests/CloudTests.cs` (new)
- Modify `/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/tests/BreakBox.Tests.csproj` — insert a `<Compile>` after line 10 (`BreakBoxTypes.cs`; verified 2026-08-16)
- Modify `/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/tests/Program.cs` — register the suite next to `BoxTests.Run();` (line 47 today; Phase 1 registers its own suites in the same block first, so append after the last `*.Run();`)
- Modify `/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/scripts/check.sh` line 28 — the `FILES=(...)` array must list `BreakBoxCloud`, or the NT8 compilation unit never sees the file

**Interfaces:**

*Consumes* (all from Phase 1, already on disk):
```csharp
public struct BbBar { public DateTime Time; public double Open, High, Low, Close, Volume; }   // BreakBoxTypes.cs:23
public sealed class BbGateReport { public string Block; public string BlockDetail; public int GateDepth;
                                   public void Set(string block, string detail, int depth); public void Clear(); }
public enum BbEntryEngine { Break = 0, Retrace = 1, Cloud = 2 }                               // BreakBoxCore.cs:35
public struct BbAction { ... public BbEntryEngine Engine; ... }                               // BreakBoxCore.cs:129
```

*Produces:*
```csharp
public sealed class BbCloudConfig            // §5.4, every horizon a BAR COUNT
public sealed class BbCloudState             // the contract's state, Ext defaulted to NaN
public sealed class BbCloud {
    public BbCloud(BbCloudConfig cfg, BbCloudState st);          // SIZES st.SlopeBuf — Phase 5 relies on this
    public BbAction OnBar(BbBar bar, int secs, double eF, double eS, double eT,
                          double atr, bool atrWarm, bool canTrade, bool positioned);
    public static readonly string[] GateLadder;                  // index == BbGateReport.GateDepth
}
```

**The cloud gate ladder — the panel phase derives its row labels from this table, not from a copy:**

| depth | `Block` | meaning |
|---|---|---|
| 0 | `warmup` | ATR/EMA not warm, or the eT slope ring not yet full |
| 1 | `regime` | `RegimeLatched == 0` (Task 21) |
| 2 | `token` | no armed pullback token (Task 22) |
| 3 | `pullback` | `AgeBars < MinPullback` (Task 24) |
| 4 | `cooldown` | `BarsSinceLastArm < MinBarsBetween` (Task 24) |
| 5 | `reclaim` | gate (a), `close` vs `eF` (Task 24) |
| 6 | `direction` | gate (b), `close` vs `open` (Task 24) |
| 7 | `body` | gate (c), `BbMath.CloseInRange` (Task 24) |
| 8 | `range` | gate (d), `MinBarRangeAtr` (Task 24) |
| 9 | `leg` | gate (e), `MinLegAtr` (Task 24) |

`suppressed` is written at depth 3 with the detail `canTrade` / `positioned` (§5.2 step 1b). It is not a ladder row: §9.3 shell-level blocks replace the headline, so the panel never renders it as a label. `BbAction.BoxHigh/BoxLow/BoxId` stay **0** for every cloud action (§4.1) — the panel must not read them when `Engine == Cloud`.

- [ ] **Step 1: Write the failing test**

```csharp
// CloudTests — the cloud engine (§5), driven the way the shell drives it: one
// CLOSED bar at a time with the ribbon values passed in as plain doubles. The
// EMAs are NOT computed here on purpose — the engine takes eF/eS/eT as numbers,
// so every geometry a test needs is one literal instead of 60 warmup bars.
using System;
using BreakBoxCore;

public static class CloudTests
{
    public static void Run()
    {
        WarmupBlocks();
    }

    private static readonly DateTime Open = new DateTime(2026, 8, 3, 18, 0, 0);

    private static DateTime Tm(int i) { return Open.AddSeconds(30 * i); }
    private static int Secs(DateTime t) { return t.Hour * 3600 + t.Minute * 60 + t.Second; }

    private static BbBar Bar(int i, double o, double h, double l, double c)
    {
        return new BbBar { Time = Tm(i), Open = o, High = h, Low = l, Close = c, Volume = 100 };
    }

    private static BbCloudConfig Cfg()
    {
        var c = new BbCloudConfig();
        c.TickSize = 0.25;
        c.TrendSlopeLookback = 5;       // short so the ring fills in 6 bars, not 11
        c.TrendSlopeAtr = 0.15;
        c.RegimeMemory = 50;
        c.PullbackMax = 50;
        return c;
    }

    // One clean uptrend bar: eT rising 0.5/bar, ribbon stacked above it, close
    // above eF, and the LOW deliberately kept above eS so this helper never
    // mints a pullback token by accident.
    private static BbAction Up(BbCloud eng, int i, double eT, double atr)
    {
        double eS = eT + 2.0, eF = eS + 2.0, c = eF + 2.0;
        return eng.OnBar(Bar(i, c - 1.0, c + 0.5, eS + 1.0, c), Secs(Tm(i)),
                         eF, eS, eT, atr, true, true, false);
    }

    private static void WarmupBlocks()
    {
        T.Section("Cloud — warmup gate (§5.2 step 1)");

        var cfg = Cfg();
        var st = new BbCloudState();
        var eng = new BbCloud(cfg, st);

        // The constructor owns the ring size. A hard-coded 12 slots silently
        // under-reads the moment the converted lookback exceeds it (§5.1).
        T.CheckInt(st.SlopeBuf.Length, cfg.TrendSlopeLookback + 1, "the constructor sized the slope ring");
        T.Check(double.IsNaN(st.Ext), "ext starts NaN — 0.0 is a price, not 'no token'");

        // atrWarm false: the shell has already ANDed in every EMA the active
        // config reads (§11 B13), so one false is the whole warmup story.
        var a = eng.OnBar(Bar(0, 100, 101, 99, 100), Secs(Tm(0)), 104, 102, 100, 4.0, false, true, false);
        T.Check(!a.Fire, "a cold engine never fires");
        T.Check(st.Gate.Block == "warmup", "and it says warmup, not 'ready' (§9.1: READY next to (out of band) is the defect)");
        T.CheckInt(st.Gate.GateDepth, 0, "warmup is ladder depth 0");

        // Warm indicators, ring still filling: the engine's OWN warmup, which the
        // shell cannot see because it does not own the ring.
        for (int i = 1; i <= 5; i++)
            Up(eng, i, 100.0 + 0.5 * i, 4.0);
        T.Check(st.Gate.Block == "warmup", "an unfilled slope ring is still warmup");

        // Sixth push fills a 6-slot ring, so the warmup gate clears.
        Up(eng, 6, 103.0, 4.0);
        T.Check(string.IsNullOrEmpty(st.Gate.Block), "a full ring clears the warmup block");

        // The ring is pushed BEFORE the warmup return. Gate the push behind the
        // gate and it never fills, so warmup never clears — a deadlock that
        // looks exactly like v1's zero-trade silence.
        T.CheckInt(st.SlopeFilled, st.SlopeBuf.Length, "the ring filled while the gate was blocking");
    }
}
```

Register it:

```csharp
// tests/Program.cs — inside Main(), after the existing *.Run() calls
        CloudTests.Run();
```

```xml
<!-- tests/BreakBox.Tests.csproj — after the BreakBoxTypes.cs line -->
    <Compile Include="../ninjascript/BreakBoxCloud.cs" Condition="Exists('../ninjascript/BreakBoxCloud.cs')" />
```

```bash
# scripts/check.sh:28
FILES=(BreakBoxTypes BreakBoxCloud BreakBoxCore BreakBoxExits BreakBoxStrategy BreakBoxPanel)
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests
```

Expected: compile failure, `error CS0246: The type or namespace name 'BbCloudConfig' could not be found` (and the same for `BbCloudState`, `BbCloud`).

- [ ] **Step 3: Write minimal implementation**

```csharp
// BreakBoxCloud.cs — Engine A, the cloud. The reference's `Signal` engine and
// the PRIMARY one: every trade in the reference session came from it, and v1's
// mistake was building the box first (§1).
//
// ZERO `using NinjaTrader.*`, namespace BreakBoxCore, C# 7.3 — same rules and
// same reason as BreakBoxTypes.cs: this exact file compiles inside NT8's single
// Custom assembly AND in the net8 test runner, and the behaviour has to be
// identical in both.
//
// The engine sees the ribbon as three DOUBLES, not as indicator objects. That is
// deliberate: the shell owns the EMAs, so a test can state a geometry in one
// line instead of warming 52 bars to reach it.
using System;

namespace BreakBoxCore
{
    public sealed class BbCloudConfig
    {
        public double TickSize = 0.25;

        // EVERY horizon below is a BAR COUNT. The parameter surface carries
        // seconds and BbScale converts inside BuildConfigs (§8). A config that
        // carried seconds would mean a different horizon on every chart — which
        // is v1's dimensional error (§1) wearing a new costume.
        public int TrendSlopeLookback = 10;     // TrendSlopeSec 300 @30s
        public double TrendSlopeAtr = 0.15;
        public int RegimeMemory = 30;           // RegimeMemorySec 900 @30s
        public int PullbackMax = 20;            // PullbackMaxSec 600 @30s
        public int MinPullback = 1;             // MinPullbackSec 30 @30s
        public double CloseInRange = 0.60;
        public double MinBarRangeAtr = 0.20;
        public double MinLegAtr = 0.35;
        public int MinBarsBetween = 6;          // MinBarsBetweenSec 180 @30s
        public int TriggerLife = 4;             // TriggerLifeSec 120 @30s — BARS. Named
                                                // TriggerLife, not TriggerLifeBars: the shell
                                                // parameter is TriggerLifeSec and two names one
                                                // suffix apart is how a seconds value ends up in
                                                // a bars field.
        public int TriggerOffsetTicks = 1;
        public bool AllowLong = true;
        public bool AllowShort = true;

        // Slope over N bars scales ~linearly with bar size while ATR scales ~√,
        // so the raw comparison goes soft on higher timeframes and hard on lower
        // ones. Normalising both sides against a fixed 10-bar reference is what
        // makes the gate mean the same thing on 15s and on 2m (§8).
        public const int RefLookbackBars = 10;
    }

    // ALL cloud memory. Nothing the engine needs may live in the shell — that is
    // what lets Vision (§12) run the same engine off its own state.
    public sealed class BbCloudState
    {
        public int RegimeLatched;               // +1 / -1 / 0 — LATCHED, never instantaneous
        public int RegimeLatchedAgeBars;

        public bool Armed;                      // a pullback token is live
        public double Ext = double.NaN;         // NaN = no token. 0.0 is a price.
        public int AgeBars;
        public int BarsSinceLastArm;
        public int TriggerArmedBars;

        public readonly BbGateReport Gate = new BbGateReport();

        // Ring of eT values, sized by BbCloud's constructor.
        public double[] SlopeBuf;
        public int SlopeIdx;
        public int SlopeFilled;
    }

    public sealed class BbCloud
    {
        private readonly BbCloudConfig _cfg;
        private readonly BbCloudState _st;
        private string _killWhy = "";           // display only; feeds the gate detail and §9.4

        // The ladder, index == BbGateReport.GateDepth. The panel renders one row
        // per entry IN THIS ORDER and the block string IS the row label, so an
        // engine writing a string absent from this array renders a blank row
        // instead of a blocker.
        public static readonly string[] GateLadder =
        {
            "warmup",       // 0
            "regime",       // 1
            "token",        // 2
            "pullback",     // 3
            "cooldown",     // 4
            "reclaim",      // 5
            "direction",    // 6
            "body",         // 7
            "range",        // 8
            "leg"           // 9
        };

        public BbCloud(BbCloudConfig cfg, BbCloudState st)
        {
            _cfg = cfg;
            _st = st;

            // The ring is sized HERE, not by the caller. BuildConfigs() runs on
            // every panel toggle (§8, B5) and hands the same state to a fresh
            // engine, so a lookback that changed with a toggle must resize the
            // ring — and a resize invalidates what is in it.
            int n = (_cfg.TrendSlopeLookback < 1 ? 1 : _cfg.TrendSlopeLookback) + 1;
            if (_st.SlopeBuf == null || _st.SlopeBuf.Length != n)
            {
                _st.SlopeBuf = new double[n];
                _st.SlopeIdx = 0;
                _st.SlopeFilled = 0;
            }
        }

        // One CLOSED bar. `eF`/`eS`/`eT`/`atr` are POST-UPDATE: the shell updates
        // them with this same bar before calling in (Strategy.cs:334-336 already
        // does exactly that for the box engine), so `close > eF` compares against
        // an EMA that has already absorbed this close. Step 0 is therefore the
        // shell's job and is documented, not implemented, here.
        public BbAction OnBar(BbBar bar, int secs, double eF, double eS, double eT,
                              double atr, bool atrWarm, bool canTrade, bool positioned)
        {
            BbAction a = default(BbAction);
            a.Fire = false;
            a.Engine = BbEntryEngine.Cloud;
            a.Why = "none";

            // The one piece of step 0 that IS ours: the eT ring. Pushed before
            // the warmup return on purpose — gate the push behind the gate and
            // the ring never fills, so warmup never clears. That deadlock is
            // indistinguishable from v1's zero-trade silence.
            PushSlope(eT);
            _st.BarsSinceLastArm++;

            // Step 1 — warmup. `atrWarm` arrives pre-ANDed with every indicator
            // the active config actually reads (§11 B13); the engine sees doubles
            // and cannot re-derive warmth. The ring's readiness is ours alone.
            if (!atrWarm || atr <= 0.0 || _st.SlopeFilled < _st.SlopeBuf.Length)
            {
                _st.Gate.Set("warmup", "atr " + atr.ToString("F2")
                             + " · slope " + _st.SlopeFilled + "/" + _st.SlopeBuf.Length, 0);
                return a;
            }

            _st.Gate.Clear();
            return a;
        }

        #region Slope ring

        private void PushSlope(double v)
        {
            int len = _st.SlopeBuf.Length;
            _st.SlopeIdx = (_st.SlopeIdx + 1) % len;
            _st.SlopeBuf[_st.SlopeIdx] = v;
            if (_st.SlopeFilled < len)
                _st.SlopeFilled++;
        }

        // eT[k]: the value k bars BEFORE this one. Only meaningful once
        // SlopeFilled == length, which the warmup gate enforces.
        private double SlopeAgo(int k)
        {
            int len = _st.SlopeBuf.Length;
            return _st.SlopeBuf[((_st.SlopeIdx - k) % len + len) % len];
        }

        #endregion
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests
```

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCloud.cs tests/CloudTests.cs tests/Program.cs tests/BreakBox.Tests.csproj scripts/check.sh && git commit -m "feat(cloud): BbCloudConfig/State + engine skeleton with the warmup gate

Config carries §5.4 as bar counts; the constructor owns the slope ring size.
The eT push happens before the warmup return — gating it behind the gate is a
deadlock that reads as v1's zero-trade silence. Gate ladder published as the
index->block table the panel will read.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 21: the slope buffer and the LATCHED regime (§5.2 step 2)

**Files:**
- Modify `/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/ninjascript/BreakBoxCloud.cs` — insert step 2 after the `_st.Gate.Clear();` written in Task 20
- Modify `/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/tests/CloudTests.cs` — four new cases

**Interfaces:**

*Consumes:* `BbCloudState.SlopeBuf/SlopeIdx/SlopeFilled`, `BbCloudConfig.TrendSlopeLookback`, `.TrendSlopeAtr`, `.RegimeMemory`, `BbCloudConfig.RefLookbackBars` (Task 20).

*Produces:*
```csharp
// BbCloud, private:
private void UpdateRegime(BbBar bar, double eF, double eS, double eT, double atr);
private void ClearRegime();
// Gate: Set("regime", <detail>, 1) when RegimeLatched == 0 after the update.
// Normalisation contract, relied on by every later task and by Vision (§12):
//   slope = (eT − eT[TrendSlopeLookback]) / TrendSlopeLookback
//   need  = TrendSlopeAtr * atr / BbCloudConfig.RefLookbackBars      (RefLookbackBars = 10)
```

- [ ] **Step 1: Write the failing test**

```csharp
// add to CloudTests.Run(), after WarmupBlocks();
        RegimeLatchSurvivesTheDeepPullback();
        RegimeClearsThreeWays();
```

```csharp
    // Feeds `bars` clean uptrend bars ending at eT = eT0 + 0.5*(bars-1).
    private static int Uptrend(BbCloud eng, int i0, int bars, double eT0, double atr)
    {
        for (int k = 0; k < bars; k++)
            Up(eng, i0 + k, eT0 + 0.5 * k, atr);
        return i0 + bars;
    }

    // A pullback bar: the ribbon has CROSSED (eF below eS) so the instantaneous
    // regime is 0, while the close still sits above eT.
    private static BbAction Pull(BbCloud eng, int i, double eT, double eS, double eF,
                                 double low, double close, double atr)
    {
        return eng.OnBar(Bar(i, close + 0.5, close + 0.7, low, close), Secs(Tm(i)),
                         eF, eS, eT, atr, true, true, false);
    }

    private static void RegimeLatchSurvivesTheDeepPullback()
    {
        T.Section("Cloud — the regime LATCH (§5.2 step 2)");

        // THE test. The token is minted by a pullback that TOUCHES eS, and a
        // pullback deep enough to touch eS drags eF to or below eS within a bar
        // or two. Under an instantaneous regime that zeroes the regime and kills
        // the token BEFORE the reclaim bar it is waiting for: the engine mints
        // and destroys on the same move, every time — v1's self-cancelling latch
        // in a new costume, and v1's zero-trade failure reproduced exactly.
        // Steps 3-5 therefore read RegimeLatched, never the instantaneous value.
        var cfg = Cfg();
        var st = new BbCloudState();
        var eng = new BbCloud(cfg, st);

        int i = Uptrend(eng, 0, 10, 100.0, 4.0);        // eT 100.0 -> 104.5
        T.CheckInt(st.RegimeLatched, +1, "a clean uptrend latches long");
        T.CheckInt(st.RegimeLatchedAgeBars, 0, "and the latch is fresh");

        // Deep pullback: eF (106.5) BELOW eS (107.0) -> instantaneous regime 0.
        // Close 106.0 is still above eT (105.0), so nothing legitimate has died.
        Pull(eng, i, 105.0, 107.0, 106.5, 106.8, 106.0, 4.0);
        T.CheckInt(st.RegimeLatched, +1, "the latch HOLDS through a zeroed instantaneous regime");
        T.CheckInt(st.RegimeLatchedAgeBars, 1, "and ages instead of clearing");

        Pull(eng, i + 1, 105.5, 107.5, 106.0, 106.2, 106.4, 4.0);
        T.CheckInt(st.RegimeLatched, +1, "two bars deep, still latched");
        T.Check(st.Gate.Block != "regime", "so the regime gate is not the blocker");
    }

    private static void RegimeClearsThreeWays()
    {
        T.Section("Cloud — the three regime clears");

        // (1) the OPPOSITE regime forming.
        var st1 = new BbCloudState();
        var eng1 = new BbCloud(Cfg(), st1);
        int i = Uptrend(eng1, 0, 10, 100.0, 4.0);
        for (int k = 0; k < 8; k++)
        {
            double eT = 104.5 - 0.5 * k, eS = eT - 2.0, eF = eS - 2.0, c = eF - 2.0;
            eng1.OnBar(Bar(i + k, c + 1.0, eS - 1.0, c - 0.5, c), Secs(Tm(i + k)),
                       eF, eS, eT, 4.0, true, true, false);
        }
        T.CheckInt(st1.RegimeLatched, -1, "the opposite regime replaces the latch");

        // (2) a close THROUGH eT against the latch. The reference's deepest
        // pullback (26864.83) still sat 4.3 pts above eT and never closed
        // through it — that is the line between "pullback" and "over".
        var st2 = new BbCloudState();
        var eng2 = new BbCloud(Cfg(), st2);
        i = Uptrend(eng2, 0, 10, 100.0, 4.0);
        Pull(eng2, i, 105.0, 107.0, 106.5, 104.0, 104.5, 4.0);   // close 104.5 < eT 105.0
        T.CheckInt(st2.RegimeLatched, 0, "a close through eT against the latch clears it");

        // (3) age > RegimeMemory.
        var cfg3 = Cfg();
        cfg3.RegimeMemory = 3;
        var st3 = new BbCloudState();
        var eng3 = new BbCloud(cfg3, st3);
        i = Uptrend(eng3, 0, 10, 100.0, 4.0);
        for (int k = 0; k < 3; k++)
            Pull(eng3, i + k, 105.0, 107.0, 106.5, 106.8, 106.0, 4.0);
        T.CheckInt(st3.RegimeLatched, +1, "still latched at exactly RegimeMemory bars");
        Pull(eng3, i + 3, 105.0, 107.0, 106.5, 106.8, 106.0, 4.0);
        T.CheckInt(st3.RegimeLatched, 0, "age > RegimeMemory clears it");
        T.Check(st3.Gate.Block == "regime" && st3.Gate.GateDepth == 1, "and the ladder says regime at depth 1");
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests
```

Expected: `FAIL a clean uptrend latches long (0 vs 1)` — the skeleton never writes `RegimeLatched`.

- [ ] **Step 3: Write minimal implementation**

```csharp
            _st.Gate.Clear();

            // Step 2 — regime, LATCHED.
            UpdateRegime(bar, eF, eS, eT, atr);
            if (_st.RegimeLatched == 0)
            {
                _st.Gate.Set("regime", "flat — need close/ribbon/slope aligned", 1);
                return a;
            }

            return a;
        }

        // The latch is the fix for the single worst defect in the first draft of
        // the design. The token is minted by a pullback that TOUCHES the far edge
        // eS, and a pullback that deep normally drags eF to or below eS within a
        // bar or two. Testing the instantaneous regime would zero it there and
        // kill the token before the reclaim bar it exists to wait for: mint and
        // destroy on the same move, every time. Steps 3-5 read RegimeLatched.
        private void UpdateRegime(BbBar bar, double eF, double eS, double eT, double atr)
        {
            // Per-bar slope against a fixed 10-bar reference, so the gate carries
            // across bar sizes instead of going soft on 2m and hard on 15s (§8).
            double slope = (eT - SlopeAgo(_cfg.TrendSlopeLookback)) / _cfg.TrendSlopeLookback;
            double need = _cfg.TrendSlopeAtr * atr / BbCloudConfig.RefLookbackBars;

            int now = 0;
            if (bar.Close > eT && eF > eS && eS > eT && slope >= need) now = +1;
            else if (bar.Close < eT && eF < eS && eS < eT && slope <= -need) now = -1;

            if (now != 0)
            {
                _st.RegimeLatched = now;
                _st.RegimeLatchedAgeBars = 0;
                return;
            }

            // "Instantaneous regime went to 0" is NOT a clear — that is the whole
            // point of the latch. Only the three below clear it.
            _st.RegimeLatchedAgeBars++;
            if (_st.RegimeLatched == 0)
                return;

            bool closedThrough = _st.RegimeLatched > 0 ? bar.Close < eT : bar.Close > eT;
            if (closedThrough || _st.RegimeLatchedAgeBars > _cfg.RegimeMemory)
                ClearRegime();
        }

        private void ClearRegime()
        {
            _st.RegimeLatched = 0;
            _st.RegimeLatchedAgeBars = 0;
        }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests
```

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCloud.cs tests/CloudTests.cs && git commit -m "feat(cloud): latched regime with a lookback-normalised slope gate

The latch is the fix for the design's worst first-draft defect: an instantaneous
regime dies on the very pullback that mints the token, which is v1's zero-trade
failure rebuilt. Slope is divided by the lookback and compared against
TrendSlopeAtr*atr/10 so the gate survives a bar-size change.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 22: the pullback token — mint, deepen, kill (§5.2 steps 3 and 4)

**Files:**
- Modify `/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/ninjascript/BreakBoxCloud.cs` — steps 3/4 after the regime gate; `UpdateRegime` gains the flip-kill
- Modify `/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/tests/CloudTests.cs`

**Interfaces:**

*Consumes:* `BbCloudState.RegimeLatched` (Task 21), `.Armed/.Ext/.AgeBars`, `BbCloudConfig.PullbackMax`.

*Produces:*
```csharp
private void KillToken(string why);   // Armed=false, Ext=NaN, AgeBars=0, TriggerArmedBars=0
// Gate: Set("token", "no token — " + <why|waiting>, 2)
// Invariant the later tasks depend on: Armed == true  =>  !double.IsNaN(Ext)
// Kill reasons: "closed through E50" | "pullback too old" | "regime flipped" | "regime lost"
```

- [ ] **Step 1: Write the failing test**

```csharp
// add to CloudTests.Run()
        TokenMintAndElseIf();
        TokenKills();
```

```csharp
    private static void TokenMintAndElseIf()
    {
        T.Section("Cloud — token mint, and the three things the else-if buys");

        var cfg = Cfg();
        var st = new BbCloudState();
        var eng = new BbCloud(cfg, st);
        int i = Uptrend(eng, 0, 10, 100.0, 4.0);

        // (a) the touch bar itself. eS = 107.0, low 106.6 touches it.
        Pull(eng, i, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        T.Check(st.Armed, "a touch of the FAR edge mints the token");
        T.CheckInt(st.AgeBars, 0, "the touch bar has AgeBars == 0 — §2: never on the touch");
        T.CheckClose(st.Ext, 106.6, "ext is the touch bar's low");

        // (c) a re-touch DEEPENS ext but does NOT reset the clock. Without the
        // else-if, price riding the ribbon resets AgeBars forever and defeats
        // PullbackMax — the token never ages out and fires days later.
        Pull(eng, i + 1, 105.5, 107.5, 106.5, 107.2, 107.4, 4.0);
        T.CheckInt(st.AgeBars, 1, "a non-touch bar ages the token");
        Pull(eng, i + 2, 106.0, 108.0, 107.0, 106.1, 107.6, 4.0);
        T.CheckInt(st.AgeBars, 2, "a RE-touch ages it too — it does not reset the clock");
        T.CheckClose(st.Ext, 106.1, "and the re-touch deepens ext");

        // (b) ext is ASSIGNED on mint, never min()-ed into a stale value. Kill
        // this token, then mint a HIGHER one: a fold would leave 106.1 behind and
        // gate (e)'s leg would be measured from a price this pullback never saw.
        Pull(eng, i + 3, 106.0, 108.0, 107.0, 105.0, 105.5, 4.0);   // close < eT -> kill
        T.Check(!st.Armed, "closing through eT killed it");
        int j = Uptrend(eng, i + 4, 10, 110.0, 4.0);                 // re-latch long
        Pull(eng, j, 114.5, 116.5, 116.0, 116.2, 116.4, 4.0);
        T.Check(st.Armed, "a new touch mints a new token");
        T.CheckClose(st.Ext, 116.2, "ext is ASSIGNED, not min()-ed into the dead token's 106.1");
    }

    private static void TokenKills()
    {
        T.Section("Cloud — the token kills (§5.2 step 4)");

        // (1) close through eT against the latch.
        var st1 = new BbCloudState();
        var eng1 = new BbCloud(Cfg(), st1);
        int i = Uptrend(eng1, 0, 10, 100.0, 4.0);
        Pull(eng1, i, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        T.Check(st1.Armed, "armed");
        Pull(eng1, i + 1, 105.0, 107.0, 106.5, 104.0, 104.5, 4.0);
        T.Check(!st1.Armed, "a close through eT kills the token");
        T.Check(double.IsNaN(st1.Ext), "and ext goes NaN — 0.0 would pass gate (e) as a real price");
        T.Check(st1.Gate.Block == "token" && st1.Gate.GateDepth == 2, "the ladder says token at depth 2");

        // (2) AgeBars > PullbackMax.
        var cfg2 = Cfg();
        cfg2.PullbackMax = 3;
        var st2 = new BbCloudState();
        var eng2 = new BbCloud(cfg2, st2);
        i = Uptrend(eng2, 0, 10, 100.0, 4.0);
        Pull(eng2, i, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        for (int k = 1; k <= 3; k++)
            Pull(eng2, i + k, 105.0, 107.0, 106.5, 107.4, 107.6, 4.0);
        T.Check(st2.Armed, "still armed at exactly PullbackMax");
        Pull(eng2, i + 4, 105.0, 107.0, 106.5, 107.4, 107.6, 4.0);
        T.Check(!st2.Armed && double.IsNaN(st2.Ext), "AgeBars > PullbackMax kills it");

        // (3) the latch FLIPPING sign. A long token in a short regime would fire
        // the wrong way with an ext that is a low.
        var st3 = new BbCloudState();
        var eng3 = new BbCloud(Cfg(), st3);
        i = Uptrend(eng3, 0, 10, 100.0, 4.0);
        Pull(eng3, i, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        T.Check(st3.Armed, "armed long");
        for (int k = 0; k < 8; k++)
        {
            double eT = 104.5 - 0.5 * k, eS = eT - 2.0, eF = eS - 2.0, c = eF - 2.0;
            eng3.OnBar(Bar(i + 1 + k, c + 1.0, eS - 1.0, c - 0.5, c), Secs(Tm(i + 1 + k)),
                       eF, eS, eT, 4.0, true, true, false);
        }
        T.CheckInt(st3.RegimeLatched, -1, "the latch flipped");
        T.Check(!st3.Armed && double.IsNaN(st3.Ext), "and the flip killed the long token");
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests
```

Expected: `FAIL a touch of the FAR edge mints the token` — nothing writes `Armed` yet.

- [ ] **Step 3: Write minimal implementation**

```csharp
            // Step 2 — regime, LATCHED.
            UpdateRegime(bar, eF, eS, eT, atr);
            if (_st.RegimeLatched == 0)
            {
                KillToken("regime lost");
                _st.Gate.Set("regime", "flat — need close/ribbon/slope aligned", 1);
                return a;
            }

            int dir = _st.RegimeLatched;

            // Step 3 — mint the token. A PHYSICAL touch of the FAR cloud edge is
            // the only thing that mints one, and that is the answer to "why does
            // it not fire every bar in a trend": a runaway that never comes back
            // to eS produces exactly ONE trade. Unlike v1's box latch — whose
            // clearing condition was near-impossible, which is why the engine was
            // mute — this condition is ordinary and recurrent. It throttles
            // without muting.
            bool touched = dir > 0 ? bar.Low <= eS : bar.High >= eS;
            if (!_st.Armed && touched)
            {
                _st.Armed = true;
                _st.AgeBars = 0;                            // the touch bar can never fire (§2)
                _st.Ext = dir > 0 ? bar.Low : bar.High;     // ASSIGNED, never folded into a stale ext
                _killWhy = "";
            }
            else if (_st.Armed)
            {
                // The else-if is load-bearing: a RE-touch deepens ext but must
                // not reset the clock, or price riding the ribbon defeats
                // PullbackMax forever and the token fires into a dead move.
                _st.AgeBars++;
                _st.Ext = dir > 0 ? Math.Min(_st.Ext, bar.Low) : Math.Max(_st.Ext, bar.High);
            }

            // Step 4 — kill. Armed always implies a real ext (KillToken is the
            // only writer of NaN and it disarms in the same breath), so the
            // Min/Max above can never fold into NaN.
            if (_st.Armed)
            {
                bool through = dir > 0 ? bar.Close < eT : bar.Close > eT;
                if (through)
                    KillToken("closed through E50");        // the reference's deepest pullback never did
                else if (_st.AgeBars > _cfg.PullbackMax)
                    KillToken("pullback too old");
            }

            if (!_st.Armed)
            {
                _st.Gate.Set("token", _killWhy.Length == 0
                             ? "no token — waiting for a touch of the far ribbon edge"
                             : "no token — " + _killWhy, 2);
                return a;
            }

            return a;
        }
```

In `UpdateRegime`, the flip must kill before the latch is overwritten, and `ClearRegime` must kill too:

```csharp
            if (now != 0)
            {
                // A long token in a short regime would fire the wrong way with an
                // ext that is a low. Kill BEFORE the latch is overwritten, while
                // the old direction is still readable.
                if (_st.RegimeLatched != 0 && now != _st.RegimeLatched)
                    KillToken("regime flipped");
                _st.RegimeLatched = now;
                _st.RegimeLatchedAgeBars = 0;
                return;
            }
```

```csharp
        private void KillToken(string why)
        {
            if (!_st.Armed && double.IsNaN(_st.Ext))
                return;                                     // nothing to kill; keep the older reason
            _st.Armed = false;
            // NaN, not 0.0: 0.0 is a price, and gate (e) would measure a leg from
            // it. Every comparison against NaN is false, so a resurrected token
            // fails closed instead of firing.
            _st.Ext = double.NaN;
            _st.AgeBars = 0;
            _st.TriggerArmedBars = 0;
            _killWhy = why;
        }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests
```

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCloud.cs tests/CloudTests.cs && git commit -m "feat(cloud): the pullback token — mint on a far-edge touch, deepen, kill

The else-if is pinned by three asserts: the touch bar has AgeBars==0 so it can
never fire, ext is assigned rather than min()-ed into a dead token's value, and
a re-touch deepens ext without resetting the clock that PullbackMax rides on.
Kill writes NaN, not 0.0 — 0.0 is a price and gate (e) would believe it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 23: token restore on expiry and rejection (§5.2 steps 7 and 9)

**Files:**
- Modify `/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/ninjascript/BreakBoxCloud.cs` — three public callbacks + `RestoreToken`
- Modify `/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/tests/CloudTests.cs`
- Modify `/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/ninjascript/BreakBoxStrategy.cs` — point the existing router at the cloud engine. `OnEntryRejected(BbEntryEngine, string)` and `_owningEngine` are **written and declared by Phase 1 Task 7**: re-read that method before editing and fill only its `Cloud` arm. The expiry site is the working-entry ager (`AgeWorkingEntry`, Strategy.cs:393-394 calls it in v1 — re-verify, Phase 1 moved code around it)

**Interfaces:**

*Consumes:* `BbCloudState.Armed/.Ext/.AgeBars/.TriggerArmedBars/.BarsSinceLastArm`, `.RegimeLatched`; the shell's `_owningEngine` (Phase 1 Task 7).

*Produces:*
```csharp
public void OnEntryFilled();                 // the ONLY thing that spends the token for good
public void OnEntryRejected(string reason);  // RESTORES
public void OnTriggerExpired();              // RESTORES
```
**Contract for Task 24's step-6 consume:** consume must set `Armed = false; BarsSinceLastArm = 0;` and **preserve `Ext` and `AgeBars`**. Restore refuses on `double.IsNaN(Ext)`, which is simultaneously the flip guard (every regime flip/clear kills the token first) and the already-filled guard.

- [ ] **Step 1: Write the failing test**

```csharp
// add to CloudTests.Run()
        TokenRestoreOnExpiryAndRejection();
```

```csharp
    private static void TokenRestoreOnExpiryAndRejection()
    {
        T.Section("Cloud — expiry and rejection RESTORE the token (§5.2 steps 7, 9)");

        // v1 burned the edge the moment a trigger armed: an expired, cancelled or
        // refused entry spent the box without ever trading it. That is defect B3,
        // and the same rule now applies to the cloud.
        var cfg = Cfg();
        var st = new BbCloudState();
        var eng = new BbCloud(cfg, st);
        int i = Uptrend(eng, 0, 10, 100.0, 4.0);

        Pull(eng, i, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);   // mint
        Pull(eng, i + 1, 105.5, 107.5, 106.5, 107.2, 107.4, 4.0); // age to 1
        T.Check(st.Armed && st.AgeBars == 1, "token armed, one bar old");

        // What step 6 does on a consumed trigger. Poked directly here because the
        // trigger itself lands in Task 24; this pins the contract it must honour.
        st.Armed = false;
        st.BarsSinceLastArm = 0;
        st.TriggerArmedBars = 3;

        eng.OnTriggerExpired();
        T.Check(st.Armed, "expiry RESTORES the token — it does not burn the edge (B3)");
        T.CheckClose(st.Ext, 106.6, "ext is preserved across the restore");
        T.CheckInt(st.AgeBars, 1, "and so is AgeBars — the pullback did not get younger");
        T.CheckInt(st.TriggerArmedBars, 0, "the trigger clock resets");

        st.Armed = false;                                        // consumed again
        eng.OnEntryRejected("qty<1");
        T.Check(st.Armed, "a refusal restores it too (every path in §11 B4)");

        // A flip must NOT restore: the flip already killed the token, so ext is
        // NaN and the restore has nothing to bring back. One guard, both cases.
        for (int k = 0; k < 8; k++)
        {
            double eT = 104.5 - 0.5 * k, eS = eT - 2.0, eF = eS - 2.0, c = eF - 2.0;
            eng.OnBar(Bar(i + 2 + k, c + 1.0, eS - 1.0, c - 0.5, c), Secs(Tm(i + 2 + k)),
                      eF, eS, eT, 4.0, true, true, false);
        }
        T.CheckInt(st.RegimeLatched, -1, "regime flipped short");
        eng.OnTriggerExpired();
        T.Check(!st.Armed, "a flipped regime does NOT restore a token pointing the other way");

        // And a FILLED token never comes back — a late reject after a fill would
        // resurrect a trade that already happened.
        var st2 = new BbCloudState();
        var eng2 = new BbCloud(Cfg(), st2);
        int j = Uptrend(eng2, 0, 10, 100.0, 4.0);
        Pull(eng2, j, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        T.Check(st2.Armed, "armed before the fill");
        eng2.OnEntryFilled();
        T.Check(!st2.Armed && double.IsNaN(st2.Ext), "a fill spends the token for good");
        eng2.OnEntryRejected("late reject");
        T.Check(!st2.Armed, "and a late refusal cannot resurrect it");
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests
```

Expected: compile failure, `error CS1061: 'BbCloud' does not contain a definition for 'OnTriggerExpired'`.

- [ ] **Step 3: Write minimal implementation**

```csharp
        // The shell calls this when the entry actually FILLS — not when it is
        // submitted. A fill is the ONLY thing that spends a token for good.
        // BarsSinceLastArm is deliberately untouched: step 6 zeroed it at consume
        // time, and restarting the cooldown here would silently lengthen it by
        // however many bars the stop order rested.
        public void OnEntryFilled()
        {
            _st.Armed = false;
            _st.Ext = double.NaN;
            _st.AgeBars = 0;
            _st.TriggerArmedBars = 0;
            _killWhy = "filled";
        }

        // §5.2 step 7. The trigger outlived TriggerLife and the shell cancelled
        // it. v1 burned the edge here — an expired trigger spent the box without
        // a trade, which is precisely defect B3. The pullback that minted this
        // token is still intact, so the token comes back.
        public void OnTriggerExpired()
        {
            RestoreToken("trigger expired");
        }

        // §5.2 step 9. Every refusal path in the shell routes here (§11 B4:
        // qty < 1, CancelWorkingEntry, OrderState.Rejected). A refusal is not a
        // trade and must not cost one.
        public void OnEntryRejected(string reason)
        {
            RestoreToken("rejected: " + reason);
        }

        private void RestoreToken(string why)
        {
            _st.TriggerArmedBars = 0;
            _killWhy = why;

            // One guard covers both refusals. A regime flip or clear ALWAYS kills
            // the token first (step 4), and a fill clears ext too — so a NaN ext
            // means either "the thesis is gone" or "this token already traded",
            // and neither may come back. ext and AgeBars are otherwise preserved
            // untouched: the pullback did not get younger while the order rested.
            if (_st.RegimeLatched == 0 || double.IsNaN(_st.Ext))
                return;

            _st.Armed = true;
        }
```

Shell wiring — fill in the two Cloud arms Phase 1 Task 7 left (re-read the surrounding method first; `_owningEngine` is declared there, assign it, never redeclare it):

```csharp
        // BreakBoxStrategy.cs — inside the router written by Phase 1 Task 7
        private void OnEntryRejected(BbEntryEngine engine, string reason)
        {
            // Only the engine that OWNS the working entry hears about it. Telling
            // both would restore a token the other engine never spent (§4.1).
            if (engine == BbEntryEngine.Cloud) _cloud.OnEntryRejected(reason);
            else                                _engine.OnEntryRejected(reason);
        }
```

```csharp
        // BreakBoxStrategy.cs — the working-entry ager, where the trigger dies of old age
        if (_owningEngine == BbEntryEngine.Cloud)
            _cloud.OnTriggerExpired();
```

- [ ] **Step 4: Run test to verify it passes**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests && scripts/check.sh
```

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCloud.cs ninjascript/BreakBoxStrategy.cs tests/CloudTests.cs && git commit -m "feat(cloud): expiry and rejection restore the token instead of burning it

v1 spent the edge the moment a trigger armed, so an expired, cancelled or
refused entry cost a trade that never happened — defect B3, now closed on the
cloud side too. One NaN-ext guard covers both refusals to restore: a regime flip
kills the token first, and a fill clears ext, so neither can be resurrected.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 24: The five gold-candle gates, both directions

**Files:**
- Modify: `ninjascript/BreakBoxCloud.cs` — `BbCloud.OnBar`'s step-5 placeholder (Task 23 leaves the token bookkeeping followed by a bare `return a;`) and one new private method next to it. This file is created in this phase, so it has no stable line numbers yet; every anchor below is structural.
- Test: `tests/CloudTests.cs` — two new methods plus their two lines in `CloudTests.Run()`.
- Unchanged, verified on disk: `BbMath.CloseInRange` lives in `ninjascript/BreakBoxTypes.cs` inside `public static class BbMath` (the class opens at `BreakBoxTypes.cs:160`); `tests/Program.cs:45-54` is the runner's `Main`.

**Interfaces:**
- Consumes: `BbMath.CloseInRange(BbBar bar, int dir)` (Phase 1 Task 4); `BbGateReport.Set(string block, string detail, int depth)` / `.Clear()` (Phase 1 Task 1); `BbCloudState { RegimeLatched, Armed, Ext, AgeBars, BarsSinceLastArm, Gate }` and `BbCloudConfig { CloseInRange, MinBarRangeAtr, MinLegAtr }` (Task 20); `BbCloud.OnBar(BbBar bar, int secs, double eF, double eS, double eT, double atr, bool atrWarm, bool canTrade, bool positioned)` (Task 20). `BreakBoxCloud.cs` is already in `tests/BreakBox.Tests.csproj` and in `scripts/check.sh`'s `FILES` array (Task 20).
- Produces: `private bool GoldCandle(BbBar bar, int dir, double eF, double atr)` and `private static string F2(double v)`, both private to `BbCloud`; and **the cloud gate ladder**, which the panel phase derives its row labels from. Index → `Block` string, in the order `OnBar` evaluates them:

| depth | `Block` | set by | fails when |
|---|---|---|---|
| 0 | `"atr warm"` | Task 20 | `!atrWarm \|\| !eT warm \|\| !eS warm` |
| 1 | `"regime"` | Task 21 | `RegimeLatched == 0` |
| 2 | `"token"` | Task 22/23 | `!Armed` |
| 3 | `"in trade"` | Task 25 | `positioned` |
| 4 | `"auto-trade"` | Task 25 | `!canTrade` |
| 5 | `"pullback age"` | Task 25 | `AgeBars < MinPullback` |
| 6 | `"cooldown"` | Task 25 | `BarsSinceLastArm < MinBarsBetween` |
| 7 | `"reclaim"` | **this task** | gate (a) |
| 8 | `"direction"` | **this task** | gate (b) |
| 9 | `"close-in-range"` | **this task** | gate (c) |
| 10 | `"bar range"` | **this task** | gate (d) |
| 11 | `"leg"` | **this task** | gate (e), NaN `Ext` included |

Depths 3-6 are reserved here and filled by Task 25. The bar gates keep 7-11 either way, so a panel row label never renumbers between the two tasks.

- [ ] **Step 1: Write the failing test**

Append to `tests/CloudTests.cs` (and add `GoldCandleGatesLong();` and `GoldCandleGatesShort();` to `CloudTests.Run()`):

```csharp
    // Gate tests seed a live token directly instead of driving sixty bars
    // through the ribbon. The mint, the latch and the kill have their own tests
    // in Tasks 21-23; a gate test that depends on all three fails for three
    // reasons and diagnoses none of them.
    private static BbCloudConfig GateCfg()
    {
        var c = new BbCloudConfig();
        c.TickSize = 0.25;
        c.TrendSlopeLookback = 10;
        c.TrendSlopeAtr = 0.15;
        c.RegimeMemory = 30;
        c.PullbackMax = 20;
        c.MinPullback = 1;
        c.MinBarsBetween = 6;
        c.CloseInRange = 0.60;
        c.MinBarRangeAtr = 0.20;      // ATR 4.00 -> 0.80 points
        c.MinLegAtr = 0.35;           // ATR 4.00 -> 1.40 points
        c.TriggerLife = 4;
        c.TriggerOffsetTicks = 1;
        return c;
    }

    // The cloud owns no clock — the shell passes `secs` and never reads
    // bar.Time — so these bars carry no timestamp on purpose.
    private static BbBar B(double o, double h, double l, double c)
    {
        return new BbBar { Time = DateTime.MinValue, Open = o, High = h, Low = l, Close = c, Volume = 100 };
    }

    private static BbCloud LiveToken(BbCloudConfig cfg, BbCloudState st, int dir, double ext)
    {
        st.RegimeLatched = dir;
        st.RegimeLatchedAgeBars = 0;
        st.Armed = true;
        st.Ext = ext;
        st.AgeBars = 3;               // past MinPullback, far short of PullbackMax
        st.BarsSinceLastArm = 99;     // no cooldown in the way
        return new BbCloud(cfg, st);
    }

    private static void GoldCandleGatesLong()
    {
        T.Section("Cloud — the five gold-candle gates, long");

        // Ribbon eF 101.00 / eS 100.50, trend eT 99.00, ATR 4.00. The token was
        // minted on a touch of eS and its extreme sits at 100.00. A flat eT
        // means the instantaneous regime reads 0 every bar — which is exactly
        // the case the latch exists for, so these tests also prove the gates
        // read `RegimeLatched` and never `regimeNow`.
        const double eF = 101.0, eS = 100.5, eT = 99.0, atr = 4.0;

        var cfg = GateCfg();
        var st = new BbCloudState();
        var c = LiveToken(cfg, st, +1, 100.0);
        var a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(a.Fire, "all five gates pass");
        T.CheckInt(a.Dir, +1, "long, in the latched regime's direction");
        T.Check(string.IsNullOrEmpty(st.Gate.Block), "a firing bar leaves no blocker on the ladder");

        // (a) the identical bar under a ribbon it never reclaimed.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0);
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, 103.5, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a close still inside the ribbon does not fire");
        T.Check(st.Gate.Block == "reclaim", "the ladder names the reclaim gate (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 7, "reclaim sits at depth 7");

        // (b) reclaims the ribbon, but on a down bar.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0);
        a = c.OnBar(B(103.0, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a down close does not fire a long");
        T.Check(st.Gate.Block == "direction", "the ladder names the direction gate (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 8, "direction sits at depth 8");

        // (c) same body, 2.1 points of upper wick: 1.90/4.00 = 0.475 < 0.60.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0);
        a = c.OnBar(B(101.2, 105.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a bar that gave back half its range does not fire");
        T.Check(st.Gate.Block == "close-in-range", "the ladder names close-in-range (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 9, "close-in-range sits at depth 9");

        // (d) a wickless 0.60-point bar against a 0.80-point floor.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0);
        a = c.OnBar(B(101.9, 102.5, 101.9, 102.5), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a doji-sized reclaim does not fire");
        T.Check(st.Gate.Block == "bar range", "the ladder names the bar-range gate (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 10, "bar range sits at depth 10");

        // (e) a qualifying bar whose leg from the pullback extreme is 1.30.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.4);
        a = c.OnBar(B(101.0, 101.7, 100.8, 101.65), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a 1.30-point leg misses the 1.40-point floor");
        T.Check(st.Gate.Block == "leg", "the ladder names the leg gate (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 11, "leg sits at depth 11");

        // (e) with the extreme lost. Every comparison against NaN is false, so
        // without an explicit guard this bar fires on a token that is gone.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, double.NaN);
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a NaN pullback extreme fails CLOSED, not open");
        T.Check(st.Gate.Block == "leg", "and it is reported as the leg gate (" + st.Gate.Block + ")");
    }

    private static void GoldCandleGatesShort()
    {
        T.Section("Cloud — the five gold-candle gates, short (a real mirror)");

        // Mirrored ribbon: eF 99.00 below eS 99.50 below eT 101.00, regime -1,
        // the token minted on a touch of eS from below with its extreme at
        // 100.00. Same ATR, same two floors.
        const double eF = 99.0, eS = 99.5, eT = 101.0, atr = 4.0;

        var cfg = GateCfg();
        var st = new BbCloudState();
        var c = LiveToken(cfg, st, -1, 100.0);
        var a = c.OnBar(B(98.8, 99.0, 97.0, 97.1), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(a.Fire, "all five gates pass, short");
        T.CheckInt(a.Dir, -1, "short, in the latched regime's direction");

        // (a) a close that is still above the ribbon.
        st = new BbCloudState(); c = LiveToken(cfg, st, -1, 100.0);
        a = c.OnBar(B(98.8, 99.0, 97.0, 97.1), 36000, 96.5, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a close above the ribbon does not fire a short");
        T.Check(st.Gate.Block == "reclaim", "reclaim, short (" + st.Gate.Block + ")");

        // (b) below the ribbon, but on an up bar.
        st = new BbCloudState(); c = LiveToken(cfg, st, -1, 100.0);
        a = c.OnBar(B(97.0, 99.0, 97.0, 97.1), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "an up close does not fire a short");
        T.Check(st.Gate.Block == "direction", "direction, short (" + st.Gate.Block + ")");

        // (c) measured from the HIGH for a short: (99.00-97.10)/4.00 = 0.475.
        st = new BbCloudState(); c = LiveToken(cfg, st, -1, 100.0);
        a = c.OnBar(B(98.8, 99.0, 95.0, 97.1), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a 2.1-point lower wick does not fire a short");
        T.Check(st.Gate.Block == "close-in-range", "close-in-range, short (" + st.Gate.Block + ")");

        // (d) 0.60 points of range against the same 0.80-point floor.
        st = new BbCloudState(); c = LiveToken(cfg, st, -1, 100.0);
        a = c.OnBar(B(98.1, 98.1, 97.5, 97.5), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a doji-sized reclaim does not fire a short");
        T.Check(st.Gate.Block == "bar range", "bar range, short (" + st.Gate.Block + ")");

        // (e) the leg runs DOWN from the extreme: 99.60 - 98.30 = 1.30.
        st = new BbCloudState(); c = LiveToken(cfg, st, -1, 99.6);
        a = c.OnBar(B(98.9, 99.2, 98.3, 98.35), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a 1.30-point leg misses the floor, short");
        T.Check(st.Gate.Block == "leg", "leg, short (" + st.Gate.Block + ")");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|FAILURES"`

Expected: FAIL — the step-5 placeholder never fires and never writes the ladder, so at minimum:
```
  FAIL all five gates pass
  FAIL the ladder names the reclaim gate ()
  FAIL reclaim sits at depth 7 (0 vs 7)
```

- [ ] **Step 3: Write minimal implementation**

In `BreakBoxCloud.cs`, replace the step-5 placeholder at the end of `BbCloud.OnBar` with:

```csharp
            // ---- Step 5 (§5.2). Task 25 inserts the suppression and cooldown
            // gates (depths 3-6) directly ABOVE this line; the bar gates keep
            // depths 7-11 either way, so the panel's row labels never renumber.
            if (!GoldCandle(bar, _st.RegimeLatched, eF, atr))
                return a;                       // GoldCandle wrote the ladder

            // Step 6 — the trigger price, the signal bar and the token's fate —
            // is Task 25. All this bar can say yet is that the candle qualifies.
            _st.Gate.Clear();
            a.Fire = true;
            a.Dir = _st.RegimeLatched;
            return a;
```

and add, as private members of `BbCloud`:

```csharp
        // The five gold-candle gates (§5.2 step 5), in ladder order. Each writes
        // its OWN depth, because "READY" next to a dead engine is the defect §9
        // exists to fix: the panel has to be able to name the one gate that
        // refused, not just report that something did.
        //
        // The short is a REAL mirror, not a sign flip. (c) measures the close
        // from the HIGH and (e) measures the leg DOWN from `Ext`; folding the two
        // directions into `dir * (close - open) > 0` reads clever and is wrong
        // for one of them.
        private bool GoldCandle(BbBar bar, int dir, double eF, double atr)
        {
            // (a) reclaim — price is back OUT of the ribbon in the regime's
            // direction. A CONTEXT gate, not a bar gate (§5.3): a textbook
            // engulfing bar still inside the cloud is not a signal, and the
            // first draft of the spec lost this distinction three times.
            if (dir > 0 ? bar.Close <= eF : bar.Close >= eF)
            {
                _st.Gate.Set("reclaim", "close " + F2(bar.Close) + " vs ribbon " + F2(eF), 7);
                return false;
            }

            // (b) direction — the reclaim has to be a bar in our direction. Both
            // observed signal candles were solid bodies.
            if (dir > 0 ? bar.Close <= bar.Open : bar.Close >= bar.Open)
            {
                _st.Gate.Set("direction", "close " + F2(bar.Close) + " vs open " + F2(bar.Open), 8);
                return false;
            }

            // (c) close-in-range — how much of its range the bar kept. Through
            // BbMath.CloseInRange and NEVER a hand-rolled ratio: Vision paints
            // the same candle gold from the same helper, and a second copy of
            // this arithmetic is how the picture starts lying about the engine.
            double cir = BbMath.CloseInRange(bar, dir);
            if (cir < _cfg.CloseInRange)
            {
                _st.Gate.Set("close-in-range", F2(cir) + " (need " + F2(_cfg.CloseInRange) + ")", 9);
                return false;
            }

            // (d) bar range — a reclaim printed by a doji is a tick of drift.
            // Calibrated against a trade we KNOW was taken: the reference's
            // right-hand reclaim bar was 0.30 ATR, so anything above 0.30
            // rejects a real entry (§5.4 marks the search 0.0-0.30 ONLY).
            double range = bar.High - bar.Low;
            if (range < _cfg.MinBarRangeAtr * atr)
            {
                _st.Gate.Set("bar range", F2(range) + " (need " + F2(_cfg.MinBarRangeAtr * atr) + ")", 10);
                return false;
            }

            // (e) leg — how far this bar travelled from the pullback extreme.
            // The NaN test is not defensive noise: a killed token leaves
            // `Ext = NaN`, every comparison against NaN is false, and without it
            // the `<` below fails OPEN and fires on a token that no longer
            // exists.
            if (double.IsNaN(_st.Ext))
            {
                _st.Gate.Set("leg", "no pullback extreme (token killed)", 11);
                return false;
            }
            double leg = dir > 0 ? bar.High - _st.Ext : _st.Ext - bar.Low;
            if (leg < _cfg.MinLegAtr * atr)
            {
                _st.Gate.Set("leg", F2(leg) + " from " + F2(_st.Ext)
                                    + " (need " + F2(_cfg.MinLegAtr * atr) + ")", 11);
                return false;
            }

            return true;
        }

        // Gate details are read by a human on a chart, so they are formatted
        // invariantly rather than under NT8's UI culture: "0,42" in a ladder
        // that elsewhere prints "0.60" reads as two different quantities.
        private static string F2(double v)
        {
            return v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -20`

Expected: PASS — `ALL PASS (n checks)` from the runner and `compiles clean` from nt8c.

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCloud.cs tests/CloudTests.cs && git commit -m "feat(cloud): the five gold-candle gates, written and tested in both directions

Reclaim, direction, close-in-range, bar range and leg, at ladder depths 7-11 so
the panel can name the ONE gate that refused. Close-in-range routes through
BbMath.CloseInRange so Vision and the engine cannot drift, and gate (e) tests
double.IsNaN(Ext) explicitly — every comparison against NaN is false, so a
killed token would otherwise fail open and fire on an extreme that is gone.

The short is a real mirror with its own asserts, not a comment claiming symmetry.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 25: The trigger, the token's owner, and the `canTrade` boundary

**Files:**
- Modify: `ninjascript/BreakBoxCloud.cs` — the cooldown counter (immediately after Task 20's step-1 warmup gate), the four suppression/cooldown gates (immediately above Task 24's `GoldCandle` call), the step-6 action fill (replacing Task 24's four-line stub), and `OnEntryFilled()` (Task 20).
- Test: `tests/CloudTests.cs` — two new methods plus their two lines in `CloudTests.Run()`.

**Interfaces:**
- Consumes: `BbCloudConfig { TickSize, TriggerOffsetTicks, MinPullback, MinBarsBetween }` (Task 20); `BbCloudState { Armed, Ext, AgeBars, BarsSinceLastArm, TriggerArmedBars, Gate }` (Task 20); `BbMath.RoundToTick(double px, double tick)` (`BreakBoxTypes.cs:165`); `BbAction { Fire, Dir, Engine, TriggerPx, IsLimit, SignalBarHigh, SignalBarLow, BoxHigh, BoxLow, BoxId, Why }` (`BreakBoxCore.cs:129-141`); `BbEntryEngine.Cloud` (`BreakBoxCore.cs`, widened by Phase 1 Task 7); `BbCloud.OnEntryRejected(string)` / `OnTriggerExpired()` (Task 23 — both restore the token; neither is touched here).
- Produces: ladder depths 3-6 of the table published in Task 24 — `"in trade"` (3), `"auto-trade"` (4), `"pullback age"` (5), `"cooldown"` (6); a fully-populated cloud `BbAction` with `Engine = BbEntryEngine.Cloud`, `IsLimit = false`, `BoxHigh = BoxLow = 0.0`, `BoxId = 0`, `Why = "cloud_long" | "cloud_short"`; and the rule that **`OnBar` never consumes the token — `OnEntryFilled()` does.**

- [ ] **Step 1: Write the failing test**

Append to `tests/CloudTests.cs` (and add `TriggerAndTokenOwnership();` and `CanTradeBoundaryAndCooldown();` to `CloudTests.Run()`):

```csharp
    private static void TriggerAndTokenOwnership()
    {
        T.Section("Cloud — the trigger, and who spends the token");

        const double eF = 101.0, eS = 100.5, eT = 99.0, atr = 4.0;
        var cfg = GateCfg();

        var st = new BbCloudState();
        var c = LiveToken(cfg, st, +1, 100.0);
        var a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(a.Fire, "the qualifying bar fires");
        // A STOP one tick beyond the signal bar's high. Both observed fills were
        // WORSE than the signal — that is a stop being taken out, not a limit
        // being hit, and a limit here is a different model that wins the
        // mean-reverting cases and loses every real continuation.
        T.CheckClose(a.TriggerPx, 103.25, "trigger sits one tick above the signal bar's high");
        T.Check(!a.IsLimit, "the cloud entry is a stop, not a limit");
        T.Check(a.Engine == BbEntryEngine.Cloud, "stamped as the cloud engine");
        T.CheckClose(a.SignalBarHigh, 103.0, "signal bar high feeds the Candle stop");
        T.CheckClose(a.SignalBarLow, 101.0, "signal bar low feeds the Candle stop");
        // §4.1: the panel must not read box fields on a cloud action, so they
        // are zeroed rather than left carrying whatever the struct had.
        T.CheckClose(a.BoxHigh, 0.0, "no box high on a cloud action");
        T.CheckClose(a.BoxLow, 0.0, "no box low on a cloud action");
        T.CheckInt(a.BoxId, 0, "no box id on a cloud action");

        // The action was RETURNED, not accepted. The shell may still refuse it
        // (§4.1 suppression, qty < 1, a platform rejection), so OnBar must not
        // have spent anything.
        T.Check(st.Armed, "returning an action does not consume the token");
        T.CheckClose(st.Ext, 100.0, "and it does not forget the pullback extreme");

        c.OnEntryFilled();
        T.Check(!st.Armed, "the FILL consumes the token");
        T.CheckInt(st.BarsSinceLastArm, 0, "and starts the cooldown");

        // Short mirror: one tick BELOW the signal bar's low.
        st = new BbCloudState(); c = LiveToken(cfg, st, -1, 100.0);
        a = c.OnBar(B(98.8, 99.0, 97.0, 97.1), 36000, 99.0, 99.5, 101.0, atr, true, true, false);
        T.Check(a.Fire, "the short fires");
        T.CheckClose(a.TriggerPx, 96.75, "short trigger sits one tick below the signal bar's low");
        T.Check(a.Why == "cloud_short", "and says which engine and side it came from");

        // Suppressed by an open position: no action, and the token survives for
        // the next opportunity instead of being burned by a bar we could not act
        // on anyway.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0);
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, true);
        T.Check(!a.Fire, "positioned suppresses the trigger");
        T.Check(st.Armed, "and leaves the token intact");
        T.Check(st.Gate.Block == "in trade", "ladder: in trade (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 3, "in trade sits at depth 3");

        // Suppressed by AUTO-TRADE off / lockout / outside the window.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0);
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, false, false);
        T.Check(!a.Fire, "canTrade == false suppresses the trigger");
        T.Check(st.Armed, "and leaves the token intact");
        T.Check(st.Gate.Block == "auto-trade", "ladder: auto-trade (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 4, "auto-trade sits at depth 4");
    }

    private static void CanTradeBoundaryAndCooldown()
    {
        T.Section("Cloud — MinBarsBetween, and ten minutes with AUTO-TRADE off");

        const double eF = 101.0, eS = 100.5, eT = 99.0, atr = 4.0;
        var cfg = GateCfg();                    // MinBarsBetween = 6

        // Cooldown: the counter ticks at the top of the bar, so a state seeded
        // at 4 reads 5 on this bar and 6 on the next.
        var st = new BbCloudState();
        var c = LiveToken(cfg, st, +1, 100.0);
        st.BarsSinceLastArm = 4;
        var a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a qualifying bar inside the cooldown does not fire");
        T.Check(st.Gate.Block == "cooldown", "ladder: cooldown (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 6, "cooldown sits at depth 6");
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36060, eF, eS, eT, atr, true, true, false);
        T.Check(a.Fire, "and fires on the bar the cooldown expires");

        // The pullback-age floor: the touch bar itself can never fire (§5.2
        // step 3), and MinPullback is the floor above it.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0);
        st.AgeBars = 0; cfg.MinPullback = 3;
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a token younger than MinPullback does not fire");
        T.Check(st.Gate.Block == "pullback age", "ladder: pullback age (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 5, "pullback age sits at depth 5");
        cfg.MinPullback = 1;

        // §5.2 step 1b — the boundary that matters. Five bars with AUTO-TRADE
        // off must age the token and the cooldown exactly as if we were
        // trading; anything else means re-enabling resumes from stale state and
        // the first live bar is evaluated against a ten-minute-old picture.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0);
        for (int i = 0; i < 5; i++)
        {
            a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000 + 30 * i, eF, eS, eT, atr, true, false, false);
            T.Check(!a.Fire, "blackout bar " + (i + 1) + " does not fire");
        }
        T.CheckInt(st.RegimeLatched, +1, "the latch survived the blackout");
        T.Check(st.Armed, "the token survived the blackout");
        T.CheckInt(st.AgeBars, 8, "the token kept ageing while we could not trade");
        T.CheckInt(st.BarsSinceLastArm, 104, "and so did the cooldown");

        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36150, eF, eS, eT, atr, true, true, false);
        T.Check(a.Fire, "re-enabling trades the very next qualifying bar");
        T.CheckInt(st.AgeBars, 9, "with no gap in the token's age");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|FAILURES"`

Expected: FAIL — Task 24 left `TriggerPx` at 0 and wrote no suppression gates:
```
  FAIL trigger sits one tick above the signal bar's high (0 vs 103.25)
  FAIL stamped as the cloud engine
  FAIL positioned suppresses the trigger
  FAIL ladder: in trade ()
```

- [ ] **Step 3: Write minimal implementation**

In `BreakBoxCloud.cs`, immediately after Task 20's step-1 warmup gate (before step 2's regime block):

```csharp
            // The cooldown clock. It ticks on EVERY closed bar past warmup,
            // including bars with AUTO-TRADE off (§5.2 step 1b): a counter that
            // only runs while we are allowed to trade means re-enabling serves a
            // cooldown measured from ten minutes ago.
            _st.BarsSinceLastArm++;
```

Immediately above Task 24's `GoldCandle` call:

```csharp
            // ---- Step 5's preconditions. `positioned` and `canTrade` suppress
            // THIS section and nothing else — steps 2-4 above already ran, so
            // the regime, the token's age and its extreme are current the moment
            // trading is re-enabled.
            if (positioned)
            {
                _st.Gate.Set("in trade", "position open or entry working", 3);
                return a;
            }
            if (!canTrade)
            {
                _st.Gate.Set("auto-trade", "off, locked out or outside the window", 4);
                return a;
            }
            // The touch bar itself has AgeBars == 0 by construction, which is
            // §2's "never on the touch"; MinPullback is the dial above it.
            if (_st.AgeBars < _cfg.MinPullback)
            {
                _st.Gate.Set("pullback age", _st.AgeBars + " bars, need " + _cfg.MinPullback, 5);
                return a;
            }
            // Throttle, not mute. A runaway trend that never touches eS again
            // produces one trade; this stops the OTHER failure, a cluster of
            // near-identical entries inside one pullback.
            if (_st.BarsSinceLastArm < _cfg.MinBarsBetween)
            {
                _st.Gate.Set("cooldown", _st.BarsSinceLastArm + " bars since the last arm, need "
                                         + _cfg.MinBarsBetween, 6);
                return a;
            }
```

and replace Task 24's four-line stub with the step-6 action fill:

```csharp
            // ---- Step 6 (§5.2). A STOP beyond the signal bar's extreme: both
            // observed fills were WORSE than the signal, which is a stop being
            // taken out. TriggerOffsetTicks is a G dial (§2.3) because the
            // measurement cannot tell "at the high" from "high + 1 tick".
            int dir = _st.RegimeLatched;
            double tick = _cfg.TickSize;
            double trig = dir > 0
                ? BbMath.RoundToTick(bar.High + _cfg.TriggerOffsetTicks * tick, tick)
                : BbMath.RoundToTick(bar.Low - _cfg.TriggerOffsetTicks * tick, tick);

            _st.Gate.Clear();
            _st.TriggerArmedBars = 0;

            a.Fire = true;
            a.Dir = dir;
            a.Engine = BbEntryEngine.Cloud;
            a.TriggerPx = trig;
            a.IsLimit = false;
            a.SignalBarHigh = bar.High;
            a.SignalBarLow = bar.Low;
            // §4.1: there is no box behind a cloud action, and a stale box id
            // would render on the panel as if there were.
            a.BoxHigh = 0.0;
            a.BoxLow = 0.0;
            a.BoxId = 0;
            a.Why = dir > 0 ? "cloud_long" : "cloud_short";

            // The token is NOT spent here. The shell can still suppress this
            // action (§4.1), size it to zero, or have it rejected by the broker
            // — and a trade that never happened must not cost a token. Only
            // OnEntryFilled spends it; OnTriggerExpired and OnEntryRejected
            // restore it.
            return a;
```

and `OnEntryFilled()` becomes the one place that consumes:

```csharp
        // The fill — and only the fill — spends the token. Counting a submit
        // here is how a day of cancelled entries silently becomes a day of no
        // trades (§11 B3, the same defect the box engine had).
        public void OnEntryFilled()
        {
            _st.Armed = false;
            _st.Ext = double.NaN;
            _st.AgeBars = 0;
            _st.BarsSinceLastArm = 0;
            _st.TriggerArmedBars = 0;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -20`

Expected: PASS — `ALL PASS (n checks)` from the runner and `compiles clean` from nt8c.

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCloud.cs tests/CloudTests.cs && git commit -m "feat(cloud): the trigger, and the rule that only a FILL spends the token

TriggerPx is a stop one tick beyond the signal bar's extreme, the signal bar
feeds the Candle stop, and the box fields are zeroed because §4.1 forbids the
panel from reading them on a cloud action.

OnBar returns the action WITHOUT consuming: the shell can still suppress, size
to zero or be rejected, and a trade that never happened must not cost a token.
canTrade and positioned suppress step 5 alone — the cooldown and the token's age
keep counting through a blackout, so re-enabling AUTO-TRADE resumes on a current
picture instead of a ten-minute-old one, with asserts on both.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 26: Wire the cloud into the shell — fields, the seconds surface, and the ribbon

**Files:**
- Modify: `ninjascript/BreakBoxStrategy.cs` — fields (after `private Ema _ma, _e50;` at `:100` and the panel-override block at `:134-137`), `SetDefaults` (after `RetraceOffsetTicks = 0;` at `:191`), `State.Configure` (`:234-238`), `State.DataLoaded` (`:239-258`), `BuildConfigs()` (`:272-322`), `OnBarUpdate` (after `_e50.Update(bar.Close);` at `:336`), and the `03. Engines` property group (after `RetraceOffsetTicks` at `:920-922`). **Line numbers verified against the tree on 2026-08-16; Phase 1 edits this file first, so re-grep before applying.**
- Test: `scripts/check.sh` — the shell is not in the net8 runner, so nt8c compiling all files together is the gate.

**Interfaces:**
- Consumes: `BbScale.Bars(int horizonSecs, int barSec, int min)` (Phase 1 Task 1); `private int _barSec;` and `private int BarSeconds()` (Phase 1 Task 2/3) — `_barSec` is measured at `DataLoaded` **before** `BuildConfigs()`, which divides by it; `TriggerLifeSec` (the NinjaScriptProperty **and** its `SetDefaults` seed of 120 are owned by Phase 1 Task 3 — do not add either); `BbCloudConfig`, `BbCloudState`, `BbCloud(BbCloudConfig, BbCloudState)` (Tasks 20-25).
- Produces: `private BbCloudConfig _cloudCfg; private BbCloudState _cloudState; private BbCloud _cloud; private Ema _emaFast, _emaSlow, _emaTrend; private bool _uiCloudOn;`, and thirteen new `NinjaScriptProperty` dials in `GroupName = "03. Engines"` (Orders 11-23 and 25; Order 24 is Phase 1's `TriggerLifeSec`): `EnableCloud`, `RibbonFastSec`, `RibbonSlowSec`, `TrendLineSec`, `TrendSlopeSec`, `TrendSlopeAtr`, `RegimeMemorySec`, `PullbackMaxSec`, `MinPullbackSec`, `CloseInRange`, `MinBarRangeAtr`, `MinLegAtr`, `MinBarsBetweenSec`, `TriggerOffsetTicks`.
  **For Task 27:** the ribbon is read as `_emaFast.Value`, `_emaSlow.Value`, `_emaTrend.Value`, its warmth as `_emaFast.IsWarm` etc., and the engine toggle as `_uiCloudOn`.
  **`BbCloudState.SlopeBuf` is NOT sized here** — `BbCloud`'s constructor owns it, and Phase 5 relies on that.

- [ ] **Step 1: Write the failing test**

The consumer that names what does not exist yet. In `OnBarUpdate`, after `_e50.Update(bar.Close);` (`:336`):

```csharp
            // §5.2 step 0 — the ribbon absorbs THIS closed bar BEFORE any gate
            // reads it. Under the other convention `close > eF` compares a close
            // against an EMA that has not seen it yet, which is a materially
            // looser reclaim test, not a rounding difference.
            _emaFast.Update(bar.Close);
            _emaSlow.Update(bar.Close);
            _emaTrend.Update(bar.Close);
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -20`

Expected: FAIL with
```
BreakBoxCombined.cs(...): error CS0103: The name '_emaFast' does not exist in the current context
BreakBoxCombined.cs(...): error CS0103: The name '_emaSlow' does not exist in the current context
BreakBoxCombined.cs(...): error CS0103: The name '_emaTrend' does not exist in the current context
```

- [ ] **Step 3: Write minimal implementation**

Fields, after `private Ema _ma, _e50;` (`:100`):

```csharp
        private BbCloudConfig _cloudCfg;
        private BbCloudState _cloudState;
        private BbCloud _cloud;
        // The ribbon. Built ONCE at DataLoaded from the CONVERTED periods: a
        // panel toggle rebuilds the config, and an Ema rebuilt with it would be
        // cold — an engine that stops trading for half an hour because somebody
        // clicked a button.
        private Ema _emaFast, _emaSlow, _emaTrend;
```

and with the other panel overrides, after `private bool _uiAutoTrade = true;` (`:134`):

```csharp
        private bool _uiCloudOn;
```

`SetDefaults`, after `RetraceOffsetTicks = 0;` (`:191`):

```csharp
                // ---- Cloud (§5.4). M rows are measurements off the reference
                // chart at 30s; G rows are honest guesses, and §13 step 3 tunes
                // at most THREE of them. TriggerLifeSec is seeded by the scaling
                // phase, not here.
                EnableCloud = true;
                RibbonFastSec = 300;
                RibbonSlowSec = 690;
                TrendLineSec = 1560;
                TrendSlopeSec = 300;
                TrendSlopeAtr = 0.15;
                RegimeMemorySec = 900;
                PullbackMaxSec = 600;
                MinPullbackSec = 30;
                CloseInRange = 0.60;
                MinBarRangeAtr = 0.20;
                MinLegAtr = 0.35;
                MinBarsBetweenSec = 180;
                TriggerOffsetTicks = 1;
```

`State.Configure` (`:236-238`):

```csharp
                _bracket = new BbBracket();
                _engState = new BbEngineState();
                _cloudState = new BbCloudState();
```

`State.DataLoaded` (`:241-253`) — `_barSec` first, because `BuildConfigs()` divides by it:

```csharp
                _barSec = BarSeconds();
                BuildConfigs();
                _engine = new BbEngine(_cfg, _engState);
                // BbCloud's constructor sizes _cloudState.SlopeBuf from
                // _cloudCfg.TrendSlopeLookback. Nobody else may allocate it: a
                // second allocation elsewhere is how a fixed 12-slot buffer
                // silently under-reads a converted lookback of 20.
                _cloud = new BbCloud(_cloudCfg, _cloudState);
                _atr = new WilderAtr(AtrPeriod);
                _ma = new Ema(MaPeriod);
                _e50 = new Ema(E50Period);
                _emaFast = new Ema(_cloudCfg.RibbonFast);
                _emaSlow = new Ema(_cloudCfg.RibbonSlow);
                _emaTrend = new Ema(_cloudCfg.TrendLine);
                _swings = new SwingDetector(SwingStrength);

                _uiCloudOn = EnableCloud;
                _uiBreakOn = EnableBreak;
```

`BuildConfigs()`, appended after the `_exitCfg` block and **before** the `if (_engine != null)` handover (`:318-321`):

```csharp
            // ---- Cloud (§5.4 -> §8). Every horizon arrives in SECONDS and is
            // converted HERE rather than at DataLoaded, because BuildConfigs
            // also runs on every panel toggle (Panel.cs:96-155) — a toggle that
            // rebuilt a raw config would hand the engine a 300-BAR ribbon.
            //
            // The SAME config object is refilled instead of replaced: BbCloud
            // holds a reference to it and to the state whose slope buffer was
            // sized from it, so handing over a fresh object mid-session would
            // re-allocate that buffer and blind the regime gate for
            // TrendSlopeLookback bars. A panel click must not stop the engine
            // trading for five minutes.
            if (_cloudCfg == null)
                _cloudCfg = new BbCloudConfig();
            _cloudCfg.TickSize = TickSize;
            _cloudCfg.RibbonFast = BbScale.Bars(RibbonFastSec, _barSec, 2);
            _cloudCfg.RibbonSlow = BbScale.Bars(RibbonSlowSec, _barSec, 2);
            _cloudCfg.TrendLine = BbScale.Bars(TrendLineSec, _barSec, 2);
            _cloudCfg.TrendSlopeLookback = BbScale.Bars(TrendSlopeSec, _barSec, 2);
            _cloudCfg.TrendSlopeAtr = TrendSlopeAtr;
            _cloudCfg.RegimeMemory = BbScale.Bars(RegimeMemorySec, _barSec, 2);
            _cloudCfg.PullbackMax = BbScale.Bars(PullbackMaxSec, _barSec, 2);
            _cloudCfg.MinPullback = BbScale.Bars(MinPullbackSec, _barSec, 1);
            _cloudCfg.CloseInRange = CloseInRange;
            _cloudCfg.MinBarRangeAtr = MinBarRangeAtr;
            _cloudCfg.MinLegAtr = MinLegAtr;
            _cloudCfg.MinBarsBetween = BbScale.Bars(MinBarsBetweenSec, _barSec, 1);
            _cloudCfg.TriggerLife = BbScale.Bars(TriggerLifeSec, _barSec, 1);
            _cloudCfg.TriggerOffsetTicks = TriggerOffsetTicks;
```

Properties, after `RetraceOffsetTicks` (`:920-922`):

```csharp
        [NinjaScriptProperty]
        [Display(Name = "Enable Cloud engine", Description = "§5 — the primary engine. The reference panel reads Signal ON / Break OFF", Order = 11, GroupName = "03. Engines")]
        public bool EnableCloud { get; set; }

        [NinjaScriptProperty, Range(30, 7200)]
        [Display(Name = "Cloud: ribbon fast (sec)", Description = "M — 300s = EMA(10) at 30s. Frozen by §13 step 3", Order = 12, GroupName = "03. Engines")]
        public int RibbonFastSec { get; set; }

        [NinjaScriptProperty, Range(30, 7200)]
        [Display(Name = "Cloud: ribbon slow (sec)", Description = "M — 690s = EMA(23) at 30s. The token is minted on a touch of THIS edge", Order = 13, GroupName = "03. Engines")]
        public int RibbonSlowSec { get; set; }

        [NinjaScriptProperty, Range(30, 14400)]
        [Display(Name = "Cloud: trend line (sec)", Description = "M — 1560s = EMA(52) at 30s. A close through it against the regime kills both", Order = 14, GroupName = "03. Engines")]
        public int TrendLineSec { get; set; }

        [NinjaScriptProperty, Range(30, 3600)]
        [Display(Name = "Cloud: slope lookback (sec)", Description = "G — 300s, search 150-600", Order = 15, GroupName = "03. Engines")]
        public int TrendSlopeSec { get; set; }

        [NinjaScriptProperty, Range(0.0, 5.0)]
        [Display(Name = "Cloud: slope (ATR per 10 bars)", Description = "G — 0.15, search 0.05-0.40. The reference cleared it by 7x", Order = 16, GroupName = "03. Engines")]
        public double TrendSlopeAtr { get; set; }

        [NinjaScriptProperty, Range(30, 14400)]
        [Display(Name = "Cloud: regime memory (sec)", Description = "G — how long the latch outlives a zeroed instantaneous regime", Order = 17, GroupName = "03. Engines")]
        public int RegimeMemorySec { get; set; }

        [NinjaScriptProperty, Range(30, 7200)]
        [Display(Name = "Cloud: pullback max (sec)", Description = "G — past this the touch is old news and the token dies", Order = 18, GroupName = "03. Engines")]
        public int PullbackMaxSec { get; set; }

        [NinjaScriptProperty, Range(0, 3600)]
        [Display(Name = "Cloud: min pullback (sec)", Description = "G — the touch bar itself can never fire; this is the floor above it", Order = 19, GroupName = "03. Engines")]
        public int MinPullbackSec { get; set; }

        [NinjaScriptProperty, Range(0.0, 1.0)]
        [Display(Name = "Cloud: close in range", Description = "C — 0.60. Both reference signal candles were wickless (1.00)", Order = 20, GroupName = "03. Engines")]
        public double CloseInRange { get; set; }

        [NinjaScriptProperty, Range(0.0, 0.30)]
        [Display(Name = "Cloud: min bar range (ATR)", Description = "C — search 0.0-0.30 ONLY: the reference's reclaim bar was 0.30 ATR", Order = 21, GroupName = "03. Engines")]
        public double MinBarRangeAtr { get; set; }

        [NinjaScriptProperty, Range(0.0, 5.0)]
        [Display(Name = "Cloud: min leg (ATR)", Description = "G — 0.35, search 0.20-0.80. Measured from the pullback extreme", Order = 22, GroupName = "03. Engines")]
        public double MinLegAtr { get; set; }

        [NinjaScriptProperty, Range(0, 7200)]
        [Display(Name = "Cloud: min bars between (sec)", Description = "G — 180s. Throttles a cluster of entries inside one pullback", Order = 23, GroupName = "03. Engines")]
        public int MinBarsBetweenSec { get; set; }

        [NinjaScriptProperty, Range(0, 10)]
        [Display(Name = "Cloud: trigger offset (ticks)", Description = "G — the evidence is ambiguous at 0 vs 1 (§2.3), default 1", Order = 25, GroupName = "03. Engines")]
        public int TriggerOffsetTicks { get; set; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -20`

Expected: PASS — `ALL PASS (n checks)` from the runner and `compiles clean` from nt8c.
Conformance grep, expected to print nothing (no horizon on the cloud surface is a bar count):
`grep -nE 'public int (Ribbon|Trend|Regime|Pullback|MinPullback|MinBarsBetween)[A-Za-z]*Bars' ninjascript/BreakBoxStrategy.cs`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxStrategy.cs && git commit -m "feat(shell): the cloud engine's fields, its seconds-only dial surface, and the ribbon

Fourteen dials, not one of them a bar count (§8). BuildConfigs converts them
through BbScale.Bars with the cached _barSec, and it REFILLS the same config
object instead of replacing it: BbCloud holds a reference to that object and to
the state whose slope buffer was sized from it, so a panel toggle that handed
over a fresh one would blind the regime gate for a whole lookback.

The three ribbon EMAs are built once at DataLoaded from the converted periods
and updated before any gate reads them (§5.2 step 0), so a toggle cannot re-warm
them mid-session.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

The shell now holds a fully-configured, fully-fed cloud engine that nothing calls: `_cloud.OnBar` has no call site yet, and `_uiCloudOn` gates nothing. Task 27 adds the §4.1 arbitration block — Cloud evaluated first, Box only if the cloud did not fire, `_owningEngine` recorded on submit so expiry and rejection reach exactly one engine — and that is where `_emaFast.Value` / `_emaSlow.Value` / `_emaTrend.Value` and the `_uiCloudOn` toggle are read.

---

### Task 27: Shell — §4.1 arbitration, `_owningEngine`, and the one trigger clock

**Files:**
- Modify: `ninjascript/BreakBoxStrategy.cs:385-394` (OnBarUpdate arbitration), `:457-483` (SubmitEntry), `:506-524` (AgeWorkingEntry / CancelWorkingEntry), `:728-736` (the Rejected branch)
- Test: `scripts/check.sh`

**Interfaces:**
- Consumes: `BbCloud.OnBar / OnEntryFilled / OnEntryRejected / OnTriggerExpired`, `_cloudCfg.TriggerLife` (Task 26); `BbEngine.OnBar(bar, secs, sessionDate, atr, atrWarm, positioned)` — the CURRENT six-argument signature at `BreakBoxCore.cs:201`.
- Produces: `private BbEntryEngine _owningEngine;` and `private void OnEntryRejected(BbEntryEngine engine, string reason);`. **Note for the box-engine phase:** when `BbEngine` is rewritten to the contract signature (`canTrade` added, `OnEntryRejected` / `OnTriggerExpired` gained), the call site written here is the one to update, and the `else` arm of `OnEntryRejected` is the one to fill in.

- [ ] **Step 1: Write the failing test — the arbitration block that names what does not exist yet**

Replace `BreakBoxStrategy.cs:385-394` (from `bool canTrade = ...` through the `else if (_entryPending)` arm) with:

```csharp
            bool canTrade = _uiAutoTrade && !_lockout && _atr.IsWarm && _e50.IsWarm;
            bool positioned = _inTrade || _entryPending;

            // Warm means EVERY indicator the cloud reads. It cannot ride on
            // canTrade: §5.2 step 1b requires steps 2-4 to run with AUTO-TRADE
            // off, and a cold EMA has to suppress all five.
            bool cloudWarm = _atr.IsWarm && _eF.IsWarm && _eS.IsWarm && _eT.IsWarm;

            // The cloud has no clock of its own by design, so the shell supplies
            // the entry window. The box engine tests it internally (Core.cs:225).
            bool windowOpen = BbMath.InWindow(secs, BbMath.HhmmToSecs(EntryWindowStartHhmm),
                                                    BbMath.HhmmToSecs(EntryWindowEndHhmm));

            // §4.1 — ONE live trigger and ONE position across both engines, and a
            // FIXED order: Cloud, then Box. The first Fire wins and the other is
            // not evaluated on that bar. Two engines racing for one position is
            // undefined behaviour, and undefined behaviour gets invented by
            // whoever implements it next.
            var a = _cloud.OnBar(bar, secs, _eF.Value, _eS.Value, _eT.Value,
                                 _atr.Value, cloudWarm, canTrade && windowOpen, positioned);
            if (!a.Fire)
                a = _engine.OnBar(bar, secs, sessionDate, _atr.Value, _atr.IsWarm, positioned);

            if (ShowBox) DrawBox();

            if (canTrade && a.Fire && !positioned)
                SubmitEntry(a);
            else if (_entryPending)
                AgeWorkingEntry();
```

- [ ] **Step 2: Run the gate to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -20`
Expected: PASS at this point (nothing undeclared yet) — the failure arrives in Step 4, once `SubmitEntry` names `_owningEngine`. Run it anyway to confirm the arbitration itself compiles before touching the order path.

- [ ] **Step 3: Record the owner on submit, and refuse through the callback**

`SubmitEntry`, replacing the `qty < 1` guard at `:459-461`:

```csharp
            int qty = SizedQty();
            if (qty < 1)
            {
                // A reachable refusal (§11 B4). The engine must get its token back
                // or a sizing accident silently costs a trade.
                OnEntryRejected(a.Engine, "qty<1");
                return;
            }
```

and inside the "written BEFORE the submit" block at `:479-483`:

```csharp
            // Written BEFORE the submit: NT8 can deliver the fill in-stack.
            _entryPending = true;
            _dir = a.Dir;
            _qty = qty;
            _entrySig = sig;
            _pendingAction = a;
            _entryBarsWaiting = 0;
            // §4.1 — expiry, cancellation and rejection are forwarded to THIS
            // engine and to no other. Two engines restoring one token is how a
            // single refusal turns into two trades.
            _owningEngine = a.Engine;
```

- [ ] **Step 4: Run the gate to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -20`
Expected: FAIL with `error CS0103: The name '_owningEngine' does not exist in the current context` and `error CS1501: No overload for method 'OnEntryRejected' takes 2 arguments`

- [ ] **Step 5: Declare the owner and the router**

`BreakBoxStrategy.cs`, next to the trade/order state fields (after `private int _entryBarsWaiting;` at `:116`):

```csharp
        // Which engine owns the working trigger (§4.1). Defaults to Cloud because
        // Cloud is evaluated first; it is overwritten on every submit.
        private BbEntryEngine _owningEngine = BbEntryEngine.Cloud;
```

and in the `#region Entry`, after `SizedQty()` (`:531`):

```csharp
        // The single refusal path (§11 B4). Every way an entry can fail to become
        // a trade routes here: qty < 1, a cancelled working entry, and the
        // OrderState.Rejected branch. It restores the owning engine's token,
        // because a trade that never happened must not cost one.
        private void OnEntryRejected(BbEntryEngine engine, string reason)
        {
            Print("BreakBox: entry refused (" + engine + ": " + reason + ")");
            if (engine == BbEntryEngine.Cloud && _cloud != null)
                _cloud.OnEntryRejected(reason);
            // The box engine gains its own OnEntryRejected in the box rewrite
            // (§6.2). Until then a refused box entry still burns its edge — that
            // is B3 unchanged, not a defect introduced here.
        }
```

- [ ] **Step 6: One trigger clock, mirrored by the shell**

Replace `AgeWorkingEntry` and the head of `CancelWorkingEntry` (`:506-524`):

```csharp
        // The working entry does not live forever. §11 B6: ONE clock — the number
        // is the owning engine's converted TriggerLife, and the shell only mirrors
        // it by cancelling the order that went with the trigger. Two clocks
        // counting from two different events (arm vs submit) is what let an
        // expired trigger and a live order disagree in v1.
        private void AgeWorkingEntry()
        {
            _entryBarsWaiting++;
            int life = _owningEngine == BbEntryEngine.Cloud ? _cloudCfg.TriggerLife : TriggerLifeBars;
            if (_entryBarsWaiting <= life)
                return;
            CancelWorkingEntry("expired");
        }

        private void CancelWorkingEntry(string why)
        {
            if (!_entryPending)
                return;
            _entryPending = false;
            _entryBarsWaiting = 0;
            if (_entryOrder != null && (_entryOrder.OrderState == OrderState.Working
                                        || _entryOrder.OrderState == OrderState.Accepted))
                CancelOrder(_entryOrder);
            _entryOrder = null;

            // Expiry and rejection restore identically; both entry points exist
            // because the reason is what lands in the engine log and in §13's
            // per-gate counters.
            if (why == "expired" && _owningEngine == BbEntryEngine.Cloud && _cloud != null)
                _cloud.OnTriggerExpired();
            else
                OnEntryRejected(_owningEngine, why);
        }
```

- [ ] **Step 7: Route the platform rejection and the fill**

`OnOrderUpdate`, the entry arm of the Rejected branch (`:733-734`):

```csharp
                else if (sig == SigLong || sig == SigShort)
                {
                    _entryPending = false;
                    _entryBarsWaiting = 0;
                    OnEntryRejected(_owningEngine, "platform_rejected");
                }
```

`OnExecutionUpdate`, the entry-fill branch (`:674`) — the fill is what really spends the token, and it belongs to the owner:

```csharp
                if (_owningEngine == BbEntryEngine.Cloud) _cloud.OnEntryFilled();
                else _engine.OnEntryFilled();
```
(replacing the bare `_engine.OnEntryFilled();`)

- [ ] **Step 8: Run the gate to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -20`
Expected: PASS — `ALL PASS` from the runner and `compiles clean` from nt8c

- [ ] **Step 9: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxStrategy.cs && git commit -m "feat(shell): §4.1 engine arbitration — Cloud first, one owner, one trigger clock

Cloud is evaluated before Box and the first Fire wins. _owningEngine is written
before the submit (the order-event race) and every refusal path — qty<1, a
cancelled working entry, a platform rejection — routes to that engine alone, so
a refused entry gives its token back instead of burning it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 28: Deploy to the NinjaTrader Custom folder

**Files:**
- Copy: `ninjascript/BreakBoxCloud.cs`, `ninjascript/BreakBoxTypes.cs`, `ninjascript/BreakBoxCore.cs`, `ninjascript/BreakBoxStrategy.cs` → `…/NinjaTrader 8/bin/Custom/Strategies/`
- Test: the two post-deploy checks from `[[nt8-deploy-copy-files]]`

**Interfaces:**
- Consumes: everything Phase 2 built.
- Produces: nothing in the repo. This task exists because a task is not finished until the `.cs` is where Javier can press F5 — compiling with nt8c and committing does not put it there.

- [ ] **Step 1: Confirm the target and that no basename collides**

Run:
```bash
NT="/mnt/c/Users/$USER/Documents/NinjaTrader 8/bin/Custom"
ls "$NT/Strategies" | grep -i breakbox; ls "$NT/Indicators" | grep -i breakbox
```
Expected: the four v1 files under `Strategies/`, nothing under `Indicators/`. A basename present in BOTH folders is a duplicate-type clash and must be resolved before copying — the folder is decided by the `namespace`, and every Phase 2 file is `NinjaTrader.NinjaScript.Strategies` or the pure `BreakBoxCore`.

- [ ] **Step 2: Copy — a real copy, never a symlink**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
NT="/mnt/c/Users/$USER/Documents/NinjaTrader 8/bin/Custom/Strategies" && \
cp ninjascript/BreakBoxTypes.cs ninjascript/BreakBoxCore.cs ninjascript/BreakBoxCloud.cs ninjascript/BreakBoxStrategy.cs "$NT/"
```

- [ ] **Step 3: Verify byte-for-byte**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
NT="/mnt/c/Users/$USER/Documents/NinjaTrader 8/bin/Custom/Strategies" && \
for f in BreakBoxTypes BreakBoxCore BreakBoxCloud BreakBoxStrategy; do cmp "ninjascript/$f.cs" "$NT/$f.cs" && echo "OK $f"; done
```
Expected: four `OK` lines. A `cmp` difference means the copy landed somewhere else — do not report the work as deployed.

- [ ] **Step 4: Hand it over**

Tell Javier: F5 in the NinjaScript Editor, then attach to an **MNQ 30s** chart on a **full ETH** template. The cloud engine is ON by default and the box engine is unchanged from v1. Nothing here is validated — §13 step 1 is a **counting run with orders disabled**, 5-10 Replay sessions, before any number from this engine means anything.

- [ ] **Step 5: Commit (nothing to commit — verify the tree is clean)**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git status --short
```
Expected: empty. The deploy copies out of the repo and writes nothing back into it.
## Phase 3 — Box engine rewrite

### Task 40: Delete the slot machinery, stand up the v2 engine skeleton

Closes **B11** (`SlotOf` off-by-one) and **B12** (`BreakSpentDir` as a single int used as a per-direction latch) **by deletion**: the code that held both bugs stops existing. `SlotOf`, `BbBoxSource`, `HtfMinutes`, `IbStartHhmm`, `IbMinutes`, `SessionCloseHhmm`, `BreakSpentDir` and the whole Retrace engine go with it.

**Files:**
- Modify: `ninjascript/BreakBoxCore.cs:1-585` (full rewrite)
- Modify: `ninjascript/BreakBoxStrategy.cs:135`, `:172-191`, `:248-249`, `:276-293`, `:506-512`, `:799`, `:856-882`, `:888-890`, `:900-922`
- Modify: `ninjascript/BreakBoxPanel.cs:42`, `:98-105`
- Test: `tests/BoxTests.cs` (replaced wholesale — every v1 test drove the slot machine)

> **What the wholesale replacement costs, stated up front.** It deletes Phase 1's
> `CanTradeGatesArmingOnly` (T5, B2) and `TriggerClockAndRefusals` (T6, B6) along
> with the v1 slot tests. That is deliberate: both drove the old engine's
> internals. B2 and B6 are re-established against the v2 engine by **T46**
> (`canTrade` suppresses arming only, and the lifecycle above it still runs) and
> **T47** (one engine-owned trigger clock, mirrored idempotently by the shell).
> Neither bug goes unpinned for longer than this phase.

**Interfaces:**
- Consumes: `BbGateReport { string Block; string BlockDetail; int GateDepth; void Set(string,string,int); void Clear(); }` from `BreakBoxTypes.cs` (Phase 1); `BbBar`, `BbMath.RoundToTick`, `BbMath.HhmmToSecs`, `BbMath.InWindow` from `BreakBoxTypes.cs`
- Produces: `BbEngine.OnBar(BbBar, int, DateTime, double, bool, bool, bool)` — the rewrite KEEPS Phase 1's seven-argument signature (T5 added `canTrade` for B2 and edited the `:385-394` call site; this task does not touch it again); `BbEngineState` with `readonly BbGateReport Gate` and the §6.1 lifecycle fields; `BbConfig` with the §6.3 box fields **in bars**; `BbEntryEngine { Break=0, Retrace=1, Cloud=2 }`; `BbBox { High, Low, SealedAt, Valid, Id, Range }`

> Line numbers were read off disk on 2026-08-16 **before** Phases 1–2 landed. If an earlier phase already edited `BreakBoxStrategy.cs`, re-anchor each edit on the quoted text, not on the number.

- [ ] **Step 1: Write the failing test**

Replace `tests/BoxTests.cs` entirely:

```csharp
// BoxTests — the v2 box: formation over a BAR window, seal, invalidate, the
// validity gate against previously sealed boxes, the cold start, and §6.2
// arming.
//
// v1's tests drove a 240-minute slot machine (PriorPeriod / InitialBalance /
// PriorSession) that no longer exists. They were deleted with it rather than
// ported: they pinned the exact dimensional error that made the strategy take
// zero trades.
//
// Every test drives the engine the way the shell does: one CLOSED 30-second bar
// at a time, with an ET seconds-of-day and a session date. Bars are synthetic
// and hand-built so every expected number is arithmetic, not a fixture.
using System;
using BreakBoxCore;

public static class BoxTests
{
    public static void Run()
    {
        ColdStartIsHardDisabled();
    }

    // 09:30 ET, inside the default entry window.
    private static readonly DateTime Open = new DateTime(2026, 8, 3, 9, 30, 0);

    private static BbBar Bar(DateTime t, double o, double h, double l, double c)
    {
        return new BbBar { Time = t, Open = o, High = h, Low = l, Close = c, Volume = 100 };
    }

    private static int Secs(DateTime t)
    {
        return t.Hour * 3600 + t.Minute * 60 + t.Second;
    }

    private static BbConfig Cfg()
    {
        var c = new BbConfig();
        c.TickSize = 0.25;
        c.BoxLookback = 4;
        c.BoxMinBars = 2;
        c.BoxRangePctile = 50.0;
        c.BoxSampleN = 200;
        c.BoxMeanSamples = 3;
        c.BoxValidLo = 0.4;
        c.BoxValidHi = 2.5;
        c.BoxDeadAtr = 0.5;
        c.BoxMaxAge = 500;
        c.BoxArmsPerEdge = 2;
        c.BoxArmCooldown = 3;
        c.TriggerOffsetTicks = 1;
        c.TriggerLife = 1;
        c.EntryWindowStartHhmm = 930;
        c.EntryWindowEndHhmm = 1545;
        return c;
    }

    // ATR warm, auto-trade on, flat. The three booleans are the ones the shell
    // passes; `canTrade` is the B2 fix and is now an argument, not a value the
    // shell computes and throws away.
    private static BbAction Step(BbEngine eng, DateTime t, double o, double h, double l, double c, double atr)
    {
        return eng.OnBar(Bar(t, o, h, l, c), Secs(t), t.Date, atr, true, true, false);
    }

    private static void ColdStartIsHardDisabled()
    {
        T.Section("Box — cold start is hard-disabled and says so");

        var cfg = Cfg();
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        // A tape that breaks a tight range every 20 bars. With no sealed-box
        // history there is no denominator for the validity gate, so nothing may
        // fire — an undefined cold start is the second way to reproduce v1's
        // silence, and this is the assert that stops it coming back.
        int fires = 0;
        DateTime t = Open;
        for (int i = 0; i < 60; i++)
        {
            bool brk = (i % 20) == 19;
            if (Step(eng, t, 100.0, brk ? 108.5 : 100.5, 99.5, brk ? 108.0 : 100.0, 2.0).Fire)
                fires++;
            t = t.AddSeconds(30);
        }

        T.CheckInt(fires, 0, "nothing fires before BoxMeanSamples boxes have sealed");
        T.Check(st.Gate.Block == "box warming", "the gate names the cold start (got '" + st.Gate.Block + "')");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: FAIL — build errors, `error CS1501: No overload for method 'OnBar' takes 7 arguments` and `error CS0117: 'BbConfig' does not contain a definition for 'BoxLookback'`

- [ ] **Step 3: Write minimal implementation**

Replace `ninjascript/BreakBoxCore.cs` entirely:

```csharp
// BreakBoxCore.cs — Engine B, the box. Pure decision code: it never touches an
// order, never reads a clock, never does I/O.
//
// ZERO `using NinjaTrader.*`, own namespace `BreakBoxCore`, C# 7.3 only — same
// rules and same reason as BreakBoxTypes.cs.
//
// WHY v1's BOX IS DELETED RATHER THAN RETUNED. v1 built a 240-MINUTE wall-clock
// range and validated it against a 14-BAR ATR: 275 points / 7.85 = 35 ATR
// against a ceiling of 6. No box was ever valid, so no engine ever ran and the
// strategy took ZERO trades. That is a dimensional error, not a tuning problem.
// The whole slot machinery is therefore gone — SlotOf, BbBoxSource, HtfMinutes,
// IbStartHhmm, IbMinutes, SessionCloseHhmm — and with it BreakSpentDir. Deleting
// them CLOSES B11 (the SlotOf off-by-one) and B12 (BreakSpentDir, a single int
// doing the work of a per-direction latch): the code that held both bugs no
// longer exists. The Retrace engine goes too — it is retired, and only its enum
// value survives so saved workspaces do not shift.
//
// WHAT A BOX IS IN v2. A micro accumulation measured in BARS all the way down:
// the range of the last BoxLookback CLOSED bars, judged against a percentile of
// that same measurement over the recent past, then validated against the mean
// range of boxes sealed before it. Every number in the chain is a bar range over
// a bar range — dimensionless, and it survives a change of bar size, which is
// exactly what MinBoxRangeAtr / MaxBoxRangeAtr were failing to express.
//
// TIMING. Every decision is taken at a BAR CLOSE, from closed bars only, and the
// formation window EXCLUDES the bar being processed: including it is a one-bar
// lookahead that lets the range see the break it is about to be tested against.
using System;
using System.Globalization;

namespace BreakBoxCore
{
    public enum BbEntryEngine
    {
        Break = 0,
        Retrace = 1,            // retired in v2; the value is retained so saved workspaces do not shift
        Cloud = 2
    }

    // The five stop sources the target's panel exposes. Priced in BreakBoxExits;
    // named here because the core reports the signal candle that `Candle` needs.
    public enum BbStopSource
    {
        Candle = 0,
        Swing = 1,
        Ma = 2,
        Ema50 = 3,
        Manual = 4
    }

    public sealed class BbBox
    {
        public double High;
        public double Low;
        public DateTime SealedAt;       // the bar that froze the edges
        public bool Valid;              // passed the §6.1 validity gate at seal time
        public int Id;                  // monotone; arming is counted per edge PER ID

        public double Range { get { return High - Low; } }
    }

    public sealed class BbConfig
    {
        public double TickSize = 0.25;

        // --- Box lifecycle (§6.1). Every horizon here is in BARS and is
        // converted from a SECONDS parameter inside the shell's BuildConfigs().
        // BoxMinBars is the exception: it is a confirmation COUNT, not a
        // horizon, so it does not scale with bar size.
        public int BoxLookback = 7;
        public int BoxMinBars = 2;
        public double BoxRangePctile = 35.0;
        public int BoxSampleN = 200;
        public int BoxMeanSamples = 20;
        public double BoxValidLo = 0.4;
        public double BoxValidHi = 2.5;
        public double BoxDeadAtr = 0.5;
        public int BoxMaxAge = 60;
        public int BoxArmsPerEdge = 2;
        public int BoxArmCooldown = 6;

        // --- Entry. Same stop-market mechanism as the cloud engine's §5.2
        // step 6, and the SAME offset dial: two dials meaning "how far beyond
        // the bar" is one dial and one place to disagree with yourself.
        public int TriggerOffsetTicks = 1;
        public int TriggerLife = 4;

        // --- Gating
        public bool EnableBreak = true;
        public bool AllowLong = true;
        public bool AllowShort = true;
        public int MaxTradesPerBox = 1;
        // A governor of last resort, not a plan. The design frequency is 8-12
        // fills per session (§13 step 4); the daily budget only has to stop a
        // runaway loop. DailyLossLimit in the shell is the real governor,
        // because it measures HOW MUCH, not HOW MANY. Phase 1 set this to 30
        // (B9) and the rewrite carries it over verbatim — a rewrite that
        // quietly restores an old default is how a closed bug reopens.
        public int MaxTradesPerDay = 30;
        public int EntryWindowStartHhmm = 930;
        public int EntryWindowEndHhmm = 1545;
        public int AtrPeriod = 14;
    }

    public struct BbAction
    {
        public bool Fire;
        public int Dir;                 // +1 long, -1 short
        public BbEntryEngine Engine;
        public double TriggerPx;        // stop price
        public bool IsLimit;            // false => stop-market
        public double SignalBarHigh;    // the `Candle` stop source reads these two
        public double SignalBarLow;
        public double BoxHigh, BoxLow;  // 0 when Engine == Cloud
        public int BoxId;
        public string Why;
    }

    // The engine's ONLY memory. Nothing it needs may live in the shell.
    public sealed class BbEngineState
    {
        // The engine's own gate report. Never shared with the cloud engine's:
        // two engines writing one report is how a panel ends up describing the
        // wrong ladder (§4.2).
        public readonly BbGateReport Gate = new BbGateReport();

        // Formation window: the last BoxLookback CLOSED bars, EXCLUDING the bar
        // being processed. Sized from the config, never from a constant.
        public double[] WinHigh, WinLow;
        public int WinIdx, WinFilled;

        // Every bar's window range, sampled UNCONDITIONALLY (§6.1). Gating the
        // sample on the formation test feeds the percentile only ranges that
        // already passed it — a loop that tightens forever until nothing forms.
        public double[] Samples;
        public int SampleIdx, SampleFilled;

        // The candidate under formation
        public bool CandOpen;
        public int CandBars;
        public double CandHigh, CandLow;

        // The sealed box and its age, in bars
        public BbBox Box;
        public int NextBoxId = 1;
        public int BoxAge;

        // Ranges of the last BoxMeanSamples SEALED boxes — the validity
        // denominator.
        public double[] SealedRanges;
        public int SealedIdx, SealedFilled;
        public int SealedCount;                 // lifetime count; the cold start reads this

        // Arming, per edge, per box Id (§6.2)
        public int ArmsUp, ArmsDn;
        public int LastArmBar = int.MinValue / 2;
        public int BarCount;

        public bool Armed;
        public int ArmDir;
        public double ArmTriggerPx;
        public int TriggerArmedBars;

        // Counters
        public int TradesThisBox;
        public int TradesToday;
        public DateTime CountedDay = DateTime.MinValue;
    }

    public sealed class BbEngine
    {
        // The gate ladder, in the order the panel renders it:
        //   0 atr warm · 1 box warming (cold start) · 2 box · 3 box valid
        //   4 auto-trade · 5 window · 6 budget · 7 armed · 8 break · 9 arms
        private readonly BbConfig _cfg;
        private readonly BbEngineState _st;

        public BbEngine(BbConfig cfg, BbEngineState st)
        {
            _cfg = cfg;
            _st = st;
            EnsureBuffers();
        }

        public BbBox Box { get { return _st.Box; } }
        public BbEngineState State { get { return _st; } }

        // Allocate only when the size actually changed. BuildConfigs() rebuilds
        // the engine on EVERY panel toggle, and blowing the sample ring away on
        // a toggle would restart the cold start from zero and mute the engine
        // for another BoxMeanSamples boxes — a dead strategy caused by clicking
        // a button.
        private void EnsureBuffers()
        {
            int look = _cfg.BoxLookback < 1 ? 1 : _cfg.BoxLookback;
            if (_st.WinHigh == null || _st.WinHigh.Length != look)
            {
                _st.WinHigh = new double[look];
                _st.WinLow = new double[look];
                _st.WinIdx = 0;
                _st.WinFilled = 0;
            }

            int n = _cfg.BoxSampleN < 2 ? 2 : _cfg.BoxSampleN;
            if (_st.Samples == null || _st.Samples.Length != n)
            {
                _st.Samples = new double[n];
                _st.SampleIdx = 0;
                _st.SampleFilled = 0;
            }

            int m = _cfg.BoxMeanSamples < 1 ? 1 : _cfg.BoxMeanSamples;
            if (_st.SealedRanges == null || _st.SealedRanges.Length != m)
            {
                _st.SealedRanges = new double[m];
                _st.SealedIdx = 0;
                _st.SealedFilled = 0;
            }
        }

        // Feed one CLOSED bar. `secs` is its ET seconds-of-day, `sessionDate` the
        // trading day it belongs to, `canTrade` whether the shell would accept an
        // entry at all (B2: it used to be computed and discarded AFTER the engine
        // had already mutated), `positioned` whether a position or a working
        // entry already exists.
        public BbAction OnBar(BbBar bar, int secs, DateTime sessionDate,
                              double atr, bool atrWarm, bool canTrade, bool positioned)
        {
            BbAction a = default(BbAction);
            a.Fire = false;
            a.Why = "none";

            _st.BarCount++;
            RollDay(sessionDate);

            // Warmup. Every gate below reads an ATR-scaled threshold, and a
            // partially warmed ATR shrinks all of them at once — which reads as
            // "it took a trade it should not have", never as a warmup bug.
            if (!atrWarm || atr <= 0.0)
            {
                _st.Gate.Set("atr warm", "warming", 0);
                return a;
            }

            if (_st.SealedCount < _cfg.BoxMeanSamples)
            {
                _st.Gate.Set("box warming",
                             _st.SealedCount + "/" + _cfg.BoxMeanSamples + " boxes sealed", 1);
                return a;
            }

            _st.Gate.Set("box", "no sealed box", 2);
            return a;
        }

        // Called on the FILL, not on the submit: a trigger that never filled
        // consumed no budget, and counting it here is how MaxTradesPerBox = 1
        // silently becomes zero trades on a day of cancelled entries.
        public void OnEntryFilled()
        {
            _st.TradesThisBox++;
            _st.TradesToday++;
            Disarm();
        }

        private void RollDay(DateTime sessionDate)
        {
            if (_st.CountedDay == sessionDate)
                return;
            _st.CountedDay = sessionDate;
            _st.TradesToday = 0;
        }

        private void Disarm()
        {
            _st.Armed = false;
            _st.ArmDir = 0;
            _st.ArmTriggerPx = 0.0;
            _st.TriggerArmedBars = 0;
        }

        private static string F(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
```

Then the shell deletions. `BreakBoxStrategy.cs:172-191`:

```csharp
                // ---- Box and engines. Every box horizon is a SECONDS
                // parameter (§8) and is converted in BuildConfigs().
                SessionOpenHhmm = 1800;

                EnableBreak = true;
                AllowLong = true;
                AllowShort = true;
```

`BreakBoxStrategy.cs:276-293` inside `BuildConfigs()`:

```csharp
            _cfg.EnableBreak = _uiBreakOn;
            _cfg.AllowLong = _uiLongOn;
            _cfg.AllowShort = _uiShortOn;
            _cfg.MaxTradesPerBox = MaxTradesPerBox;
```

`BreakBoxStrategy.cs:135` and `:248-249`:

```csharp
        private bool _uiBreakOn, _uiLongOn, _uiShortOn;
```
```csharp
                _uiBreakOn = EnableBreak;
                _uiLongOn = AllowLong;
```

> The `OnBar` call site at `:385-394` is NOT touched here. Phase 1's T5 already
> passes `canTrade` in and the signature this rewrite ships is the one T5's call
> site already uses (B2). Editing it a second time would be two tasks claiming
> the same three lines.

`BreakBoxStrategy.cs:506-512` — the shell mirrors the engine's clock instead of owning a second one (B6):

```csharp
        private void AgeWorkingEntry()
        {
            _entryBarsWaiting++;
            if (_entryBarsWaiting <= _cfg.TriggerLife)
                return;
            CancelWorkingEntry("expired");
        }
```

`BreakBoxStrategy.cs:799`:

```csharp
            DrawTag(Draw.Rectangle(this, Tag("box"), false, box.SealedAt, box.Low, Time[0].AddHours(4),
                                   box.High, b, b, 12));
```

Delete the property blocks at `:856-862`, `:868-882`, `:888-890`, `:900-922`. Panel: drop `_retraceBtn` from `:42` and delete the toggle at `:98-105`:

```csharp
        private Button _autoBtn, _breakBtn, _buyBtn, _sellBtn, _lockBtn;
```
```csharp
                engines.Children.Add(_breakBtn);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS" && ! grep -rnE "SlotOf|BbBoxSource|HtfMinutes|IbStartHhmm|IbMinutes|SessionCloseHhmm|BreakSpentDir" ninjascript tests --include=*.cs`
Expected: PASS — `ALL PASS`, and the grep finds none of the deleted symbols

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript tests && git commit -m "refactor(core): delete the 240-minute slot machinery, close B11/B12 by deletion

The box was a wall-clock object validated against a bar-count ATR (275 pts /
7.85 = 35 ATR vs a ceiling of 6), so no box was ever valid and v1 took zero
trades. SlotOf, BbBoxSource, HtfMinutes, IbStartHhmm, IbMinutes,
SessionCloseHhmm, BreakSpentDir and the Retrace engine are gone; B11 and B12
are closed because the code that held them no longer exists.

OnBar keeps the seven-argument signature Phase 1 gave it (B2). Replacing
BoxTests.cs drops T5's and T6's asserts with the engine they drove; B2 and B6
are re-pinned against the v2 engine in the arming and trigger-clock tasks.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 41: FORMATION — the bar window and a sample ring that is not self-selected

**Files:**
- Modify: `ninjascript/BreakBoxCore.cs` (add the `Lifecycle` region to `BbEngine`)
- Test: `tests/BoxTests.cs`

**Interfaces:**
- Consumes: `BbEngineState.WinHigh/WinLow/WinIdx/WinFilled`, `Samples/SampleIdx/SampleFilled`, `CandOpen/CandBars/CandHigh/CandLow` (Task 40)
- Produces: `BbEngine.Lifecycle(BbBar, double)` (private); the invariant that **one sample is pushed on every bar with a full window, pass or fail**, and that the window excludes the bar being processed

- [ ] **Step 1: Write the failing test**

Add to `tests/BoxTests.cs` (`Run()` gains both calls):

```csharp
        SampleRingIsNotSelfSelected();
        FormationExcludesTheCurrentBar();
```

```csharp
    private static void SampleRingIsNotSelfSelected()
    {
        T.Section("Box — the sample ring is fed on EVERY bar, pass or fail");

        var cfg = Cfg();                    // BoxLookback = 4
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        DateTime t = Open;
        for (int i = 0; i < 30; i++)        // tight: range 1.0
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        for (int i = 0; i < 30; i++)        // wide: range 20.0 — fails formation every bar
        {
            Step(eng, t, 100.0, 110.0, 90.0, 100.0, 2.0);
            t = t.AddSeconds(30);
        }

        // 60 bars, the first 4 with no full window yet. If sampling were gated
        // on the formation test the 30 wide bars would contribute nothing and
        // this would read 26 — the feedback loop that tightens the percentile
        // forever until no box can ever form.
        T.CheckInt(st.SampleFilled, 56, "every bar with a full window contributes one sample");
    }

    private static void FormationExcludesTheCurrentBar()
    {
        T.Section("Box — the formation window is MAX(High,N)[1], not [0]");

        var cfg = Cfg();
        cfg.BoxMinBars = 999;               // nothing seals: this test is about FORMATION alone
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        DateTime t = Open;
        for (int i = 0; i < 30; i++)
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.CandOpen, "a quiet stretch is a candidate");
        T.CheckClose(st.CandHigh, 100.5, "candidate high");
        T.CheckClose(st.CandLow, 99.5, "candidate low");

        // A 60-point bar. Its own range may NOT enter the window it is being
        // tested against — that is the one-bar lookahead that lets a backtest
        // measure the break it is about to trade.
        Step(eng, t, 100.0, 150.0, 90.0, 100.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(st.CandOpen, "the current bar does not widen its own window");
        T.CheckClose(st.CandHigh, 100.5, "candidate high is still the quiet window's");

        // On the NEXT bar it does enter the window, and the candidate dies.
        Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
        T.Check(!st.CandOpen, "one bar later the wide bar is in the window and formation fails");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: FAIL with `FAIL every bar with a full window contributes one sample (0 vs 56)`

- [ ] **Step 3: Write minimal implementation**

In `BreakBoxCore.cs`, call `Lifecycle` from `OnBar` right after the warmup gate:

```csharp
            // The lifecycle runs on EVERY closed bar — while locked out, while
            // AUTO-TRADE is off, while positioned. The sample ring, the window
            // and a box's age describe the TAPE, not our permission to trade it;
            // suppressing them leaves a hole that re-enabling cannot fill.
            Lifecycle(bar, atr);

            if (_st.SealedCount < _cfg.BoxMeanSamples)
```

and add the region before `RollDay`:

```csharp
        #region Lifecycle

        private void Lifecycle(BbBar bar, double atr)
        {
            double hi, lo;
            bool full = WindowRange(out hi, out lo);
            Push(bar);
            if (!full)
                return;

            PushSample(hi - lo);
            Form(bar, hi, lo);
        }

        // MAX(High,N)[1] − MIN(Low,N)[1]: the window ENDS one bar back, because
        // it is read BEFORE the current bar is pushed.
        private bool WindowRange(out double hi, out double lo)
        {
            hi = 0.0;
            lo = 0.0;
            if (_st.WinFilled < _st.WinHigh.Length)
                return false;

            hi = double.MinValue;
            lo = double.MaxValue;
            for (int i = 0; i < _st.WinHigh.Length; i++)
            {
                if (_st.WinHigh[i] > hi) hi = _st.WinHigh[i];
                if (_st.WinLow[i] < lo) lo = _st.WinLow[i];
            }
            return true;
        }

        private void Push(BbBar bar)
        {
            _st.WinHigh[_st.WinIdx] = bar.High;
            _st.WinLow[_st.WinIdx] = bar.Low;
            _st.WinIdx = (_st.WinIdx + 1) % _st.WinHigh.Length;
            if (_st.WinFilled < _st.WinHigh.Length)
                _st.WinFilled++;
        }

        private void PushSample(double range)
        {
            _st.Samples[_st.SampleIdx] = range;
            _st.SampleIdx = (_st.SampleIdx + 1) % _st.Samples.Length;
            if (_st.SampleFilled < _st.Samples.Length)
                _st.SampleFilled++;
        }

        // Nearest-rank percentile over the filled part of the ring.
        // ponytail: sorts a copy every bar — 200 samples is ~1600 comparisons on
        // a 30s bar. Replace with an order-statistic structure only if a profiler
        // ever names it.
        private double Percentile(double pct)
        {
            int n = _st.SampleFilled;
            if (n < 1)
                return double.NaN;

            double[] copy = new double[n];
            Array.Copy(_st.Samples, copy, n);
            Array.Sort(copy);

            double p = pct < 0.0 ? 0.0 : (pct > 100.0 ? 100.0 : pct);
            int rank = (int)Math.Ceiling(p / 100.0 * n) - 1;
            if (rank < 0) rank = 0;
            if (rank >= n) rank = n - 1;
            return copy[rank];
        }

        private void Form(BbBar bar, double hi, double lo)
        {
            double thr = Percentile(_cfg.BoxRangePctile);
            bool candidate = !double.IsNaN(thr) && (hi - lo) <= thr;

            if (!candidate)
            {
                _st.CandOpen = false;
                _st.CandBars = 0;
                return;
            }

            if (!_st.CandOpen)
            {
                _st.CandOpen = true;
                _st.CandBars = 1;
            }
            else _st.CandBars++;

            _st.CandHigh = hi;
            _st.CandLow = lo;
        }

        #endregion
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS — `ALL PASS`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCore.cs tests/BoxTests.cs && git commit -m "feat(core): box FORMATION over a bar window against an unconditional sample ring

The percentile is taken over a ring fed on every bar. Gating the sample on the
formation test would feed the distribution only ranges that already passed it,
which tightens forever until nothing forms. The window is MAX(High,N)[1]: the
current bar is excluded, or the range sees the break it is tested against.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 42: SEAL — frozen edges, monotone Id, after BoxMinBars

**Files:**
- Modify: `ninjascript/BreakBoxCore.cs` (`Form` gains the seal branch; add `Seal`)
- Test: `tests/BoxTests.cs`

**Interfaces:**
- Consumes: `Form(BbBar, double, double)`, `BbEngineState.CandBars/CandHigh/CandLow` (Task 41)
- Produces: `BbEngineState.Box` (a `BbBox` with frozen `High`/`Low`, monotone `Id`, `SealedAt`), `BbEngineState.NextBoxId`, `BbEngineState.SealedCount`, `BbEngine.Seal(double, DateTime)` (private)

- [ ] **Step 1: Write the failing test**

Add to `tests/BoxTests.cs` (`Run()` gains `SealFreezesTheEdges();`):

```csharp
    private static void SealFreezesTheEdges()
    {
        T.Section("Box — SEAL after BoxMinBars, edges frozen, Id monotone");

        var cfg = Cfg();                    // BoxLookback 4, BoxMinBars 2
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        // Bars 1-4 fill the window. Bar 5 is the first candidate, bar 6 the
        // second consecutive one — that is the seal.
        DateTime t = Open;
        for (int i = 0; i < 5; i++)
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.Box == null, "one passing bar is not a box");

        Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(st.Box != null, "BoxMinBars consecutive passing bars seal it");
        T.CheckInt(st.Box.Id, 1, "ids are monotone from 1");
        T.CheckClose(st.Box.High, 100.5, "sealed high");
        T.CheckClose(st.Box.Low, 99.5, "sealed low");
        T.CheckInt(st.SealedCount, 1, "the seal counts toward the cold start");

        // Nothing moves a sealed box's edges, and a live box is not replaced:
        // an accumulation that gets a new identity every quiet bar has no
        // identity, and §6.2 counts arms PER BOX ID.
        for (int i = 0; i < 10; i++)
        {
            Step(eng, t, 100.0, 100.9, 99.6, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.CheckInt(st.Box.Id, 1, "a live box is not replaced");
        T.CheckClose(st.Box.High, 100.5, "and its edges did not move");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: FAIL with `FAIL BoxMinBars consecutive passing bars seal it`

- [ ] **Step 3: Write minimal implementation**

Append to `Form`, after `_st.CandLow = lo;`:

```csharp
            if (_st.CandBars < _cfg.BoxMinBars)
                return;

            // A live box is never replaced. It dies first (INVALIDATE) — an
            // object whose identity changes every quiet bar cannot carry a
            // per-Id arm budget, which is the whole of §6.2.
            if (_st.Box != null)
                return;

            // Refuse to seal a box the current bar has already left. The window
            // excludes this bar by design, so without this guard the bar that
            // breaks a box seals an identical one on the spot and the engine
            // churns Ids while price runs away.
            if (bar.Close > hi || bar.Close < lo)
                return;

            Seal(hi - lo, bar.Time);
```

and add `Seal` beneath it:

```csharp
        private void Seal(double range, DateTime t)
        {
            _st.Box = new BbBox
            {
                High = _st.CandHigh,
                Low = _st.CandLow,
                SealedAt = t,
                Valid = false,              // the validity gate fills this in (Task 44)
                Id = _st.NextBoxId++
            };
            _st.BoxAge = 0;
            _st.ArmsUp = 0;
            _st.ArmsDn = 0;
            _st.TradesThisBox = 0;
            _st.CandOpen = false;
            _st.CandBars = 0;

            _st.SealedRanges[_st.SealedIdx] = range;
            _st.SealedIdx = (_st.SealedIdx + 1) % _st.SealedRanges.Length;
            if (_st.SealedFilled < _st.SealedRanges.Length)
                _st.SealedFilled++;
            _st.SealedCount++;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS — `ALL PASS`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCore.cs tests/BoxTests.cs && git commit -m "feat(core): SEAL — a box gets frozen edges and a monotone id

A candidate seals on the BoxMinBars-th consecutive passing bar and is never
replaced while alive: arms are counted per edge PER BOX ID, so an object that
changes identity every quiet bar has no budget at all.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 43: INVALIDATE — a close beyond an edge by BoxDeadAtr, or BoxMaxAge

**Files:**
- Modify: `ninjascript/BreakBoxCore.cs` (`Lifecycle` gains `Invalidate`)
- Test: `tests/BoxTests.cs`

**Interfaces:**
- Consumes: `BbEngineState.Box`, `BoxAge` (Task 42); `BbConfig.BoxDeadAtr`, `BoxMaxAge`
- Produces: `BbEngine.Invalidate(BbBar, double)` (private); `Box == null` after death, and the next candidate seals in its place

- [ ] **Step 1: Write the failing test**

Add to `tests/BoxTests.cs` (`Run()` gains `InvalidateOnBreakAndOnAge();`):

```csharp
    private static void InvalidateOnBreakAndOnAge()
    {
        T.Section("Box — INVALIDATE on a close beyond an edge, and on age");

        // --- Killed by distance. ATR 2.0, BoxDeadAtr 0.5 -> 1.0 point of slack
        // past 100.5, so a close at 102.0 is 6 ticks beyond the tolerance.
        var cfg = Cfg();
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        DateTime t = Open;
        for (int i = 0; i < 6; i++)
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.Box != null, "sealed");

        Step(eng, t, 100.0, 101.2, 100.0, 101.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(st.Box != null, "a close inside the BoxDeadAtr tolerance does not kill it");

        Step(eng, t, 101.0, 102.5, 100.9, 102.0, 2.0);
        T.Check(st.Box == null, "a close beyond the edge by more than BoxDeadAtr kills it");

        // --- Killed by age, and replaced by the next candidate.
        var cfg2 = Cfg();
        cfg2.BoxMaxAge = 5;
        var st2 = new BbEngineState();
        var eng2 = new BbEngine(cfg2, st2);

        DateTime u = Open;
        for (int i = 0; i < 12; i++)        // seals at bar 6, ages out on bar 12
        {
            Step(eng2, u, 100.0, 100.5, 99.5, 100.0, 2.0);
            u = u.AddSeconds(30);
        }
        T.CheckInt(st2.Box.Id, 2, "an aged-out box is replaced by the next candidate to seal");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: FAIL with `FAIL a close beyond the edge by more than BoxDeadAtr kills it`

- [ ] **Step 3: Write minimal implementation**

In `Lifecycle`, between `PushSample` and `Form`:

```csharp
            PushSample(hi - lo);
            // Invalidate BEFORE forming, so the bar that buries a box can also
            // be the bar a replacement seals on — a one-bar dead zone after
            // every box is a one-bar dead zone in the only quiet tape the model
            // trades.
            Invalidate(bar, atr);
            Form(bar, hi, lo);
```

and add:

```csharp
        private void Invalidate(BbBar bar, double atr)
        {
            if (_st.Box == null)
                return;

            _st.BoxAge++;

            // The tolerance is ATR-scaled, not a tick count: one tick through an
            // edge is noise on any tape, and the same absolute number is noise on
            // one instrument and a real break on another.
            double dead = _cfg.BoxDeadAtr * atr;
            bool broken = bar.Close > _st.Box.High + dead || bar.Close < _st.Box.Low - dead;
            bool old = _st.BoxAge > _cfg.BoxMaxAge;
            if (!broken && !old)
                return;

            Disarm();
            _st.Box = null;
            _st.BoxAge = 0;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS — `ALL PASS`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCore.cs tests/BoxTests.cs && git commit -m "feat(core): INVALIDATE a box on a BoxDeadAtr close beyond an edge or on BoxMaxAge

Death runs before formation so the burying bar can also seal the replacement.
The tolerance is ATR-scaled: a tick count is noise on one instrument and a real
break on another.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 44: VALIDITY GATE — winRange over the mean of boxes sealed strictly before

**Files:**
- Modify: `ninjascript/BreakBoxCore.cs` (`Seal` computes `Valid`; add `SealedMean`, `SeedSealedRange`)
- Test: `tests/BoxTests.cs`

**Interfaces:**
- Consumes: `BbEngineState.SealedRanges/SealedIdx/SealedFilled/SealedCount` (Task 40), `Seal` (Task 42)
- Produces: `BbBox.Valid` set at seal time; `BbEngine.SealedMean()` (private); `public void SeedSealedRange(double range)` — the seam the history phase (§10) fills so day 1 is not dead

- [ ] **Step 1: Write the failing test**

Add to `tests/BoxTests.cs` (`Run()` gains `ValidityIsRelativeToEarlierBoxes();`):

```csharp
    private static void ValidityIsRelativeToEarlierBoxes()
    {
        T.Section("Box — the validity gate is a ratio against boxes sealed BEFORE it");

        // Seed one 3.0-point box. A 1.0-point box then rates 0.333, below
        // BoxValidLo = 0.4, so it is refused. If the box pushed its own range
        // into the denominator first the mean would be 2.0 and the ratio 0.5 —
        // valid. That is the discrimination this test exists for: a box that
        // helps set its own mean always looks normal.
        var cfg = Cfg();
        cfg.BoxMeanSamples = 1;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        eng.SeedSealedRange(3.0);

        DateTime t = Open;
        for (int i = 0; i < 6; i++)
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.Box != null, "sealed");
        T.Check(!st.Box.Valid, "0.33x the mean of earlier boxes is out of band");

        // Same 1.0-point box against a 1.0-point history: ratio 1.0, valid.
        var st2 = new BbEngineState();
        var eng2 = new BbEngine(cfg, st2);
        eng2.SeedSealedRange(1.0);

        DateTime u = Open;
        for (int i = 0; i < 6; i++)
        {
            Step(eng2, u, 100.0, 100.5, 99.5, 100.0, 2.0);
            u = u.AddSeconds(30);
        }
        T.Check(st2.Box.Valid, "1.0x the mean is in band");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: FAIL with `FAIL 1.0x the mean is in band`

- [ ] **Step 3: Write minimal implementation**

Rewrite the head of `Seal`:

```csharp
        private void Seal(double range, DateTime t)
        {
            // The denominator is read BEFORE this box's own range is pushed:
            // §6.1 says "boxes sealed strictly BEFORE this one", and a box that
            // contributes to its own mean always rates about 1.0 — the gate
            // would pass everything and mean nothing.
            //
            // A RATIO, not an ATR band. This is the v1 defect stated positively:
            // a bar-window range over a bar-window range is dimensionless, so it
            // carries across bar sizes and instruments untouched.
            double mean = SealedMean();
            double ratio = mean > 0.0 ? range / mean : double.NaN;
            bool valid = !double.IsNaN(ratio) && ratio >= _cfg.BoxValidLo && ratio <= _cfg.BoxValidHi;

            _st.Box = new BbBox
            {
                High = _st.CandHigh,
                Low = _st.CandLow,
                SealedAt = t,
                Valid = valid,
                Id = _st.NextBoxId++
            };
```

and add, next to `SealedMean`:

```csharp
        private double SealedMean()
        {
            if (_st.SealedFilled < 1)
                return 0.0;
            double s = 0.0;
            for (int i = 0; i < _st.SealedFilled; i++)
                s += _st.SealedRanges[i];
            return s / _st.SealedFilled;
        }

        // The cold-start seam. The shell replays box ranges from the §10 history
        // file at DataLoaded so day 1 is not a dead day. Pure: it takes a number,
        // never a file — the I/O lives in BreakBoxStrategy.cs.
        public void SeedSealedRange(double range)
        {
            if (range <= 0.0 || double.IsNaN(range))
                return;
            _st.SealedRanges[_st.SealedIdx] = range;
            _st.SealedIdx = (_st.SealedIdx + 1) % _st.SealedRanges.Length;
            if (_st.SealedFilled < _st.SealedRanges.Length)
                _st.SealedFilled++;
            _st.SealedCount++;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS — `ALL PASS`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCore.cs tests/BoxTests.cs && git commit -m "feat(core): validity gate = winRange over the mean of earlier sealed boxes

Dimensionless by construction, which is what MinBoxRangeAtr/MaxBoxRangeAtr were
trying and failing to express. The denominator is read before the box pushes its
own range, or every box rates 1.0 and the gate means nothing.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 45: COLD START — hard-disabled, and it says so

An undefined cold start is the other way to reproduce v1's silence: the validity gate has no denominator until boxes have sealed, and "no denominator" must read as a stated warmup, never as a strategy that looks READY and never trades.

**Files:**
- Modify: `ninjascript/BreakBoxCore.cs` (the cold-start gate detail; nothing else)
- Test: `tests/BoxTests.cs`

**Interfaces:**
- Consumes: `SeedSealedRange` (Task 44), `BbGateReport` (Phase 1)
- Produces: gate `Block == "box warming"`, `BlockDetail == "<n>/<N> boxes sealed"`, `GateDepth == 1` — the string §9.3 renders as `BOX WARMING — 12/20 boxes sealed`

- [ ] **Step 1: Write the failing test**

Replace `ColdStartIsHardDisabled` in `tests/BoxTests.cs` with:

```csharp
    private static void ColdStartIsHardDisabled()
    {
        T.Section("Box — cold start is hard-disabled and says so");

        var cfg = Cfg();                    // BoxMeanSamples = 3
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        // A tape that breaks a tight range every 20 bars. Boxes seal all the way
        // through — the lifecycle is never suppressed — but nothing may fire
        // until the denominator exists.
        int fires = 0;
        DateTime t = Open;
        for (int i = 0; i < 20; i++)
        {
            bool brk = (i % 10) == 9;
            if (Step(eng, t, 100.0, brk ? 108.5 : 100.5, 99.5, brk ? 108.0 : 100.0, 2.0).Fire)
                fires++;
            t = t.AddSeconds(30);
        }
        T.CheckInt(fires, 0, "nothing fires before BoxMeanSamples boxes have sealed");
        T.Check(st.Gate.Block == "box warming", "the gate names the cold start (got '" + st.Gate.Block + "')");
        T.Check(st.Gate.BlockDetail == st.SealedCount + "/3 boxes sealed",
                "and counts it (got '" + st.Gate.BlockDetail + "')");
        T.CheckInt(st.Gate.GateDepth, 1, "cold start sits at ladder depth 1");
        T.Check(st.SealedCount > 0, "boxes still sealed while the engine was disabled");

        // Seeded history clears it: day 1 is not a dead day.
        var st2 = new BbEngineState();
        var eng2 = new BbEngine(cfg, st2);
        eng2.SeedSealedRange(1.0);
        eng2.SeedSealedRange(1.0);
        eng2.SeedSealedRange(1.0);

        DateTime u = Open;
        for (int i = 0; i < 6; i++)
        {
            Step(eng2, u, 100.0, 100.5, 99.5, 100.0, 2.0);
            u = u.AddSeconds(30);
        }
        T.Check(st2.Gate.Block != "box warming", "seeded history clears the cold start");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: FAIL with `FAIL seeded history clears the cold start`

- [ ] **Step 3: Write minimal implementation**

In `OnBar`, replace the cold-start block:

```csharp
            // COLD START. Until BoxMeanSamples boxes have sealed there is no
            // denominator for the validity gate, so the engine is HARD-DISABLED
            // and the panel says exactly that. v1 printed READY next to
            // "(out of band)" and never connected the two; the user watched a
            // dead strategy for an hour. The lifecycle above still runs, which
            // is what makes this warmup finite.
            if (_st.SealedCount < _cfg.BoxMeanSamples)
            {
                _st.Gate.Set("box warming",
                             _st.SealedCount + "/" + _cfg.BoxMeanSamples + " boxes sealed", 1);
                return a;
            }

            BbBox box = _st.Box;
            if (box == null)
            {
                _st.Gate.Set("box", "no sealed box", 2);
                return a;
            }

            if (!box.Valid)
            {
                _st.Gate.Set("box valid",
                             "range " + F(box.Range) + " vs mean " + F(SealedMean())
                             + " (band " + F(_cfg.BoxValidLo) + "-" + F(_cfg.BoxValidHi) + "x)", 3);
                return a;
            }

            _st.Gate.Set("break", "close inside " + F(box.High) + " / " + F(box.Low), 8);
            return a;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS — `ALL PASS`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCore.cs tests/BoxTests.cs && git commit -m "feat(core): cold start hard-disables the box engine and reports n/N boxes sealed

The validity gate has no denominator until boxes have sealed. v1 printed READY
beside '(out of band)' and never connected them; this states the warmup in the
gate ladder and SeedSealedRange lets the history file shorten it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 46: §6.2 arming — arms per edge per box Id, with a cooldown, reset on an inside close

**Files:**
- Modify: `ninjascript/BreakBoxCore.cs` (`Lifecycle` gains `TrackInside`; `OnBar` gains the entry ladder; add `Arm`)
- Test: `tests/BoxTests.cs`

**Interfaces:**
- Consumes: `BbEngineState.ArmsUp/ArmsDn/LastArmBar/BarCount/Armed/ArmDir/ArmTriggerPx` (Task 40)
- Produces: a firing `BbAction` (`Engine = BbEntryEngine.Break`, `IsLimit = false`, `BoxHigh/BoxLow/BoxId` stamped); gate blocks `"auto-trade"` (4 — also carries engine-off), `"window"` (5), `"budget"` (6), `"armed"` (7), `"break"` (8), `"arms"` / `"cooldown"` (9)

> These strings, plus `"atr warm"` (0), `"box warming"` (1), `"box"` (2) and
> `"box valid"` (3), are the WHOLE box ladder: ten rungs, ten names, no other
> value ever passed to `Gate.Set`. The panel (§9.3) renders it by index, so this
> list is a contract, not documentation — a block string that is not on it, or
> emitted at a depth that is not its own, paints the wrong row green.

- [ ] **Step 1: Write the failing test**

Add to `tests/BoxTests.cs` (`Run()` gains `ArmingDoesNotSpendTheEdge();`):

```csharp
    private static void ArmingDoesNotSpendTheEdge()
    {
        T.Section("Box — BoxArmsPerEdge arms per edge per box, cooldown, inside-close reset");

        var cfg = Cfg();                    // ArmsPerEdge 2, ArmCooldown 3, TriggerLife 1
        cfg.BoxMeanSamples = 1;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        eng.SeedSealedRange(1.0);

        DateTime t = Open;
        for (int i = 0; i < 6; i++)         // seals a valid 100.5 / 99.5 box on bar 6
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.Box != null && st.Box.Valid, "a valid box exists");

        // Bar 7 — the first break. Close 101.00 is outside the edge but inside
        // the 1.0-point BoxDeadAtr tolerance, so the box survives to be armed
        // again.
        var a = Step(eng, t, 100.5, 101.25, 100.0, 101.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(a.Fire, "a close beyond the edge arms");
        T.CheckInt(a.Dir, +1, "long");
        T.Check(!a.IsLimit, "the box entry is a stop, not a limit");
        T.Check(a.Engine == BbEntryEngine.Break, "engine stamped");
        T.CheckClose(a.TriggerPx, 101.50, "trigger = break bar high + TriggerOffsetTicks");
        T.CheckClose(a.BoxHigh, 100.5, "the action carries the box");
        T.CheckInt(a.BoxId, st.Box.Id, "and its id");
        T.CheckInt(st.ArmsUp, 1, "one arm spent on the up edge");

        // Bar 8 — still armed, nothing fires. One live trigger at a time is the
        // §4.1 half this engine owns.
        a = Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
        T.Check(!a.Fire, "no second trigger while one is working");
        T.Check(st.Gate.Block == "armed", "and the gate says so (got '" + st.Gate.Block + "')");

        // `canTrade` false suppresses ARMING and nothing else (B2). The box is
        // still there, still valid, still aging — the engine simply may not act.
        var st2 = new BbEngineState();
        var eng2 = new BbEngine(cfg, st2);
        eng2.SeedSealedRange(1.0);
        DateTime u = Open;
        for (int i = 0; i < 6; i++)
        {
            eng2.OnBar(Bar(u, 100.0, 100.5, 99.5, 100.0), Secs(u), u.Date, 2.0, true, false, false);
            u = u.AddSeconds(30);
        }
        T.Check(st2.Box != null && st2.Box.Valid, "the lifecycle ran with auto-trade off");

        var blocked = eng2.OnBar(Bar(u, 100.5, 101.25, 100.0, 101.0), Secs(u), u.Date,
                                 2.0, true, false, false);
        T.Check(!blocked.Fire, "a break does not arm while canTrade is false");
        T.Check(st2.Gate.Block == "auto-trade", "and the gate names it (got '" + st2.Gate.Block + "')");
        T.CheckInt(st2.ArmsUp, 0, "no arm was spent");
    }
```

> The cooldown, the per-edge cap and the inside-close refill all need a trigger
> that EXPIRES, and nothing expires one until Task 47. They are asserted there,
> in `CooldownAndArmCapAcrossExpiries`, rather than written here against a clock
> that does not exist yet — a task whose tree is red on purpose is a task nobody
> can review or revert on its own.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: FAIL with `FAIL a close beyond the edge arms`

- [ ] **Step 3: Write minimal implementation**

In `Lifecycle`, after `Invalidate(bar, atr);`:

```csharp
            TrackInside(bar);
```

Add `TrackInside` next to `Invalidate`:

```csharp
        // A close within both edges of the SEALED box refills the arm budget.
        // It lives in the lifecycle, not in the entry ladder: the box's arm
        // budget is a property of the box, and freezing it while AUTO-TRADE is
        // off would hand the user a box that can never be traded again.
        private void TrackInside(BbBar bar)
        {
            if (_st.Box == null)
                return;
            if (bar.Close <= _st.Box.High && bar.Close >= _st.Box.Low)
            {
                _st.ArmsUp = 0;
                _st.ArmsDn = 0;
            }
        }
```

Replace the tail of `OnBar` (everything from the `!box.Valid` check's closing brace onward):

```csharp
            // Engine-off shares the AUTO-TRADE row rather than owning one of its
            // own. The ladder has exactly ten rungs and the panel renders them by
            // INDEX (§9.3): an eleventh block emitted at an existing depth makes
            // the panel label the wrong row, which is worse than no row at all.
            // The two are the same question anyway — "may this engine arm right
            // now" — so they differ only in the detail string.
            if (!_cfg.EnableBreak)
            {
                _st.Gate.Set("auto-trade", "box engine off", 4);
                return a;
            }

            // `canTrade` suppresses ARMING only. Everything above it — the
            // window, the samples, formation, seal, death, the inside-close
            // reset — has already run on this bar (B2).
            if (!canTrade)
            {
                _st.Gate.Set("auto-trade", "off or locked out", 4);
                return a;
            }

            if (!BbMath.InWindow(secs, BbMath.HhmmToSecs(_cfg.EntryWindowStartHhmm),
                                       BbMath.HhmmToSecs(_cfg.EntryWindowEndHhmm)))
            {
                _st.Gate.Set("window", "outside the entry window", 5);
                return a;
            }

            if (_st.TradesThisBox >= _cfg.MaxTradesPerBox || _st.TradesToday >= _cfg.MaxTradesPerDay)
            {
                _st.Gate.Set("budget", _st.TradesThisBox + "/" + _cfg.MaxTradesPerBox + " box, "
                                       + _st.TradesToday + "/" + _cfg.MaxTradesPerDay + " day", 6);
                return a;
            }

            // One live trigger and one position at a time, across both engines
            // (§4.1). The shell owns the cross-engine half; this is our half.
            if (positioned || _st.Armed)
            {
                _st.Gate.Set("armed", _st.Armed
                    ? "trigger working, " + _st.TriggerArmedBars + " bars"
                    : "already positioned", 7);
                return a;
            }

            // The break must CLOSE beyond the edge. A wick through it is the
            // single most common way a box-breakout backtest lies to you: it
            // counts the touch as a break and the reversal as bad luck. v1 made
            // that a dial; it is not one.
            bool up = bar.Close > box.High && _cfg.AllowLong;
            bool dn = bar.Close < box.Low && _cfg.AllowShort;
            if (!up && !dn)
            {
                _st.Gate.Set("break", "close " + F(bar.Close) + " inside "
                                      + F(box.High) + " / " + F(box.Low), 8);
                return a;
            }

            int dir = up ? +1 : -1;
            int arms = up ? _st.ArmsUp : _st.ArmsDn;

            // ARMING DOES NOT SPEND THE EDGE. v1 set a boolean latch on arm, so
            // an expired, cancelled or refused trigger burned the box without a
            // trade (B3). Here the edge carries BoxArmsPerEdge attempts, spaced
            // by BoxArmCooldown bars and refilled by an inside close. The
            // cooldown is what keeps this from becoming the opposite bug — a
            // sustained break re-arming on every single bar.
            if (arms >= _cfg.BoxArmsPerEdge)
            {
                _st.Gate.Set("arms", arms + "/" + _cfg.BoxArmsPerEdge + " spent on this edge", 9);
                return a;
            }

            int since = _st.BarCount - _st.LastArmBar;
            if (since < _cfg.BoxArmCooldown)
            {
                _st.Gate.Set("cooldown", (_cfg.BoxArmCooldown - since) + " bars left", 9);
                return a;
            }

            // The trigger sits beyond the BREAK BAR's extreme, not beyond the box
            // edge: on a bar that closes 12 points through the level, a trigger
            // at edge+1 tick is already deep inside the market and fills at
            // whatever the next print happens to be.
            double trig = BbMath.RoundToTick(dir > 0
                ? bar.High + _cfg.TriggerOffsetTicks * _cfg.TickSize
                : bar.Low - _cfg.TriggerOffsetTicks * _cfg.TickSize, _cfg.TickSize);

            Arm(dir, trig);

            a.Fire = true;
            a.Dir = dir;
            a.Engine = BbEntryEngine.Break;
            a.TriggerPx = trig;
            a.IsLimit = false;
            a.SignalBarHigh = bar.High;
            a.SignalBarLow = bar.Low;
            a.BoxHigh = box.High;
            a.BoxLow = box.Low;
            a.BoxId = box.Id;
            a.Why = dir > 0 ? "box_up" : "box_dn";
            _st.Gate.Clear();
            return a;
        }

        private void Arm(int dir, double trig)
        {
            _st.Armed = true;
            _st.ArmDir = dir;
            _st.ArmTriggerPx = trig;
            _st.TriggerArmedBars = 0;
            _st.LastArmBar = _st.BarCount;
            if (dir > 0) _st.ArmsUp++;
            else _st.ArmsDn++;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS — `ALL PASS`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCore.cs tests/BoxTests.cs && git commit -m "feat(core): §6.2 arming — arms per edge per box id, not a boolean latch

v1 set a latch on arm, so an expired, cancelled or refused trigger burned the
box without a trade (B3). Here the edge carries BoxArmsPerEdge attempts, counted
per edge PER BOX ID and refilled by a close back inside the sealed box.

canTrade suppresses ARMING only (B2): the window, the samples, formation, seal,
death and the inside-close reset have all already run by the time it is read.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 47: Trigger life, fills, rejections — one clock, owned by the engine

**Files:**
- Modify: `ninjascript/BreakBoxCore.cs` (`Lifecycle` gains `AgeTrigger`; add `OnTriggerExpired`, `OnEntryRejected`)
- Test: `tests/BoxTests.cs`

**Interfaces:**
- Consumes: `Arm`, `Disarm`, `BbEngineState.TriggerArmedBars` (Tasks 40, 46)
- Produces: `public void OnTriggerExpired()` (idempotent), `public void OnEntryRejected(string reason)` — the two the shell's `OnEntryRejected(BbEntryEngine, string)` router calls for `Break`; and, because expiry now exists, the §6.2 asserts Task 46 could not make: the cooldown between arms, the per-edge cap, and the inside-close refill

- [ ] **Step 1: Write the failing test**

Add to `tests/BoxTests.cs` (`Run()` gains `CooldownAndArmCapAcrossExpiries();` and `ExpirySpendsAnArmRejectionRefundsIt();`):

```csharp
    // The §6.2 half that Task 46 could not assert: every one of these steps
    // needs a trigger to EXPIRE, and until AgeTrigger exists the engine stays
    // armed forever and the box is never re-armable.
    private static void CooldownAndArmCapAcrossExpiries()
    {
        T.Section("Box — cooldown between arms, a hard cap per edge, and the inside-close refill");

        var cfg = Cfg();                    // ArmsPerEdge 2, ArmCooldown 3, TriggerLife 1
        cfg.BoxMeanSamples = 1;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        eng.SeedSealedRange(1.0);

        DateTime t = Open;
        for (int i = 0; i < 6; i++)         // seals a valid 100.5 / 99.5 box on bar 6
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }

        // Bar 7 — the first break arms. Close 101.00 is outside the edge but
        // inside the 1.0-point BoxDeadAtr tolerance, so the box survives.
        var a = Step(eng, t, 100.5, 101.25, 100.0, 101.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(a.Fire && st.ArmsUp == 1, "armed once");

        // Bar 8 — still working. Bar 9 — the trigger expires (TriggerLife 1),
        // but the cooldown is 3 bars from the ARM, so the edge is not re-armed
        // on the spot: that is what stops a sustained break re-arming every bar.
        Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
        t = t.AddSeconds(30);
        a = Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(!a.Fire, "expiry does not re-arm inside the cooldown");
        T.Check(st.Gate.Block == "cooldown", "the gate names the cooldown (got '" + st.Gate.Block + "')");

        // Bar 10 — cooldown served. The EDGE was not spent by the first arm:
        // v1's boolean latch would have burned the box here without a trade.
        a = Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(a.Fire, "the second arm of the edge fires");
        T.CheckInt(st.ArmsUp, 2, "two arms spent");

        // Bars 11-13 — expire, serve the cooldown, and find the budget gone.
        for (int i = 0; i < 3; i++)
        {
            a = Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(!a.Fire, "BoxArmsPerEdge is a hard cap per edge per box");
        T.Check(st.Gate.Block == "arms", "the gate names it (got '" + st.Gate.Block + "')");

        // A close back INSIDE the sealed box refills both counters.
        Step(eng, t, 101.0, 101.0, 99.8, 100.0, 2.0);
        t = t.AddSeconds(30);
        T.CheckInt(st.ArmsUp, 0, "an inside close of the sealed box resets the arm counters");

        a = Step(eng, t, 100.0, 101.25, 99.9, 101.0, 2.0);
        T.Check(a.Fire, "and the edge is armable again");
    }

    private static void ExpirySpendsAnArmRejectionRefundsIt()
    {
        T.Section("Box — expiry spends an arm, a refusal refunds it");

        var cfg = Cfg();
        cfg.BoxMeanSamples = 1;
        cfg.TriggerLife = 2;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        eng.SeedSealedRange(1.0);

        DateTime t = Open;
        for (int i = 0; i < 6; i++)
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        var a = Step(eng, t, 100.5, 101.25, 100.0, 101.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(a.Fire && st.Armed, "armed");

        // The ENGINE owns the clock and the shell mirrors it (B6). Two clocks —
        // one counting from arm, one from submit — is how v1 ended up believing
        // it was disarmed while a stop order still rested at the exchange.
        for (int i = 0; i < 2; i++)
        {
            Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(!st.Armed, "the engine expires its own trigger after TriggerLife");
        T.CheckInt(st.ArmsUp, 1, "expiry SPENDS the arm — the market declined a live trigger");

        // Idempotent: the shell mirrors the same expiry and must not
        // double-count it.
        eng.OnTriggerExpired();
        T.CheckInt(st.ArmsUp, 1, "the shell's mirrored expiry is a no-op");

        // A refusal is OURS, not the market's: nothing was offered, so the arm
        // comes back. The cooldown does not — that is what stops a refusal loop
        // from re-arming on the very next bar.
        for (int i = 0; i < 3; i++)
        {
            a = Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(a.Fire, "re-armed after the cooldown");
        T.CheckInt(st.ArmsUp, 2, "two arms spent");
        eng.OnEntryRejected("qty<1");
        T.Check(!st.Armed, "a refusal disarms");
        T.CheckInt(st.ArmsUp, 1, "and refunds the arm");

        // A FILL is what costs budget — not a submit.
        eng.OnEntryFilled();
        T.CheckInt(st.TradesThisBox, 1, "the fill costs box budget");
        T.CheckInt(st.TradesToday, 1, "and daily budget");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: FAIL — build error `error CS1061: 'BbEngine' does not contain a definition for 'OnTriggerExpired'`

- [ ] **Step 3: Write minimal implementation**

In `Lifecycle`, as the first statement:

```csharp
        private void Lifecycle(BbBar bar, double atr)
        {
            AgeTrigger();

            double hi, lo;
```

Add `AgeTrigger` next to `Arm`, and the two public callbacks next to `OnEntryFilled`:

```csharp
        // ONE clock, owned by the engine; the shell mirrors it (B6). v1 counted
        // BreakArmedBars from the arm and _entryBarsWaiting from the submit, and
        // neither cancelled the other's object.
        private void AgeTrigger()
        {
            if (!_st.Armed)
                return;
            _st.TriggerArmedBars++;
            if (_st.TriggerArmedBars > _cfg.TriggerLife)
                OnTriggerExpired();
        }
```

```csharp
        // The market declined a live trigger. That IS an attempt, so the arm
        // stays spent: refunding it here would let a sustained break outside the
        // box re-arm forever, which is v1's failure mode seen from the other
        // side. Idempotent, because the shell mirrors this call.
        public void OnTriggerExpired()
        {
            if (!_st.Armed)
                return;
            Disarm();
        }

        // WE refused the trade — qty < 1, a cancel, a broker rejection (B4).
        // Nothing was ever offered to the market, so the arm is refunded. The
        // COOLDOWN is not: it is the only thing standing between a repeating
        // refusal and a re-arm on every bar.
        public void OnEntryRejected(string reason)
        {
            if (!_st.Armed)
                return;
            if (_st.ArmDir > 0 && _st.ArmsUp > 0) _st.ArmsUp--;
            else if (_st.ArmDir < 0 && _st.ArmsDn > 0) _st.ArmsDn--;
            Disarm();
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS — `ALL PASS`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxCore.cs tests/BoxTests.cs && git commit -m "feat(core): one engine-owned trigger clock — expiry spends an arm, a refusal refunds it

v1 counted BreakArmedBars from the arm and _entryBarsWaiting from the submit, and
neither cancelled the other's object (B6). The engine now owns the only clock and
the shell mirrors it through an idempotent OnTriggerExpired.

Expiry SPENDS the arm — the market declined a live trigger. A refusal is ours, so
it refunds the arm but never the cooldown, which is the only thing between a
repeating refusal and a re-arm on every bar. With expiry in place, §6.2's
cooldown, per-edge cap and inside-close refill are asserted end to end.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 48: §6.3 parameter surface, in SECONDS

**Files:**
- Create: nothing
- Modify: `ninjascript/BreakBoxStrategy.cs:172-191` (SetDefaults), `:276-293` (BuildConfigs), `:856-866` (properties)
- Test: `tests/BoxTests.cs`

**Interfaces:**
- Consumes: `BbScale.Bars(int horizonSecs, int barSec, int min)` from `BreakBoxTypes.cs` (Phase 1, Task 1); `_barSec` cached at `State.DataLoaded` from `BarSeconds()` (§8, Phase 1)
- Produces: the properties `BoxLookbackSec`, `BoxMinBars`, `BoxRangePctile`, `BoxSampleN`, `BoxMeanSamples`, `BoxValidLo`, `BoxValidHi`, `BoxDeadAtr`, `BoxMaxAgeSec`, `BoxArmsPerEdge`, `BoxArmCooldownSec`, and their conversion inside `BuildConfigs()`

> **Do not declare a second `BbScale`.** Phase 1 built it in `BreakBoxTypes.cs`,
> in this same namespace, and NT8 compiles every file into one assembly: a second
> copy is `error CS0101` before it is anything else. It is also the point of §8 —
> one piece of seconds→bars arithmetic, or the dials mean different things in
> different files. This task only CALLS it.

- [ ] **Step 1: Write the failing test**

Add to `tests/BoxTests.cs` (`Run()` gains `SecondsScaleToBars();`):

```csharp
    private static void SecondsScaleToBars()
    {
        T.Section("Box — the §6.3 surface is SECONDS, converted once");

        // BoxLookbackSec 210 = the measured ~7-bar white rectangle at 30s.
        T.CheckInt(BbScale.Bars(210, 15, 2), 14, "15s bars");
        T.CheckInt(BbScale.Bars(210, 30, 2), 7, "30s bars — the reference chart");
        T.CheckInt(BbScale.Bars(210, 60, 2), 3, "1m bars");

        // 210/120 = 1, and a one-bar window has no range to speak of. The floor
        // is what keeps "escala sola" from meaning "degenerates silently".
        T.CheckInt(BbScale.Bars(210, 120, 2), 2, "2m bars clamp to the floor");

        // A tick-bar estimate can come back as 0 seconds if the estimator is
        // starved. Dividing by it would throw inside OnStateChange, where the
        // exception reads as "the strategy will not load". Phase 1's rule is
        // that a nonsense bar size returns the FLOOR, not a horizon-sized bar
        // count: an unknown bar size must degrade to the smallest honest window,
        // never to a 180-bar one that looks like a real setting.
        T.CheckInt(BbScale.Bars(180, 0, 2), 2, "a zero bar size floors");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"; grep -c "BoxLookbackSec\|BoxMaxAgeSec\|BoxArmCooldownSec" ninjascript/BreakBoxStrategy.cs`
Expected: `ALL PASS` from the runner and `0` from the grep. The arithmetic is Phase 1's and already correct — what is missing is the SURFACE: after Task 40 the box engine runs entirely on `BbConfig`'s C# defaults, so the user has no box dials at all and the same hardcoded bar counts mean 3.5 minutes on a 30s chart and 7 on a 1m one. That grep returning 0 is this task's red.

- [ ] **Step 3: Write minimal implementation**

`BreakBoxStrategy.cs:172-191` SetDefaults:

```csharp
                // ---- Box (§6.3). Every horizon is SECONDS and is converted in
                // BuildConfigs(): a bar count on this surface is a parameter
                // that silently means something different on every chart.
                SessionOpenHhmm = 1800;
                BoxLookbackSec = 210;           // C — the measured ~7-bar white rectangle at 30s
                BoxMinBars = 2;                 // a confirmation COUNT, not a horizon
                BoxRangePctile = 35;
                BoxSampleN = 200;
                BoxMeanSamples = 20;
                BoxValidLo = 0.4;
                BoxValidHi = 2.5;
                BoxDeadAtr = 0.5;
                BoxMaxAgeSec = 1800;
                BoxArmsPerEdge = 2;
                BoxArmCooldownSec = 180;

                EnableBreak = true;
                AllowLong = true;
                AllowShort = true;
```

`BreakBoxStrategy.cs:276-293` BuildConfigs:

```csharp
            // The conversion lives HERE, not at DataLoaded: BuildConfigs() is
            // called again by every panel toggle, and a toggle that rebuilt a
            // raw config would hand the engine seconds where it expects bars.
            _cfg.BoxLookback = BbScale.Bars(BoxLookbackSec, _barSec, 2);
            _cfg.BoxMinBars = BoxMinBars;
            _cfg.BoxRangePctile = BoxRangePctile;
            _cfg.BoxSampleN = BoxSampleN;
            _cfg.BoxMeanSamples = BoxMeanSamples;
            _cfg.BoxValidLo = BoxValidLo;
            _cfg.BoxValidHi = BoxValidHi;
            _cfg.BoxDeadAtr = BoxDeadAtr;
            _cfg.BoxMaxAge = BbScale.Bars(BoxMaxAgeSec, _barSec, 2);
            _cfg.BoxArmsPerEdge = BoxArmsPerEdge;
            _cfg.BoxArmCooldown = BbScale.Bars(BoxArmCooldownSec, _barSec, 1);
            _cfg.TriggerOffsetTicks = TriggerOffsetTicks;
            _cfg.TriggerLife = BbScale.Bars(TriggerLifeSec, _barSec, 1);
            _cfg.EnableBreak = _uiBreakOn;
            _cfg.AllowLong = _uiLongOn;
            _cfg.AllowShort = _uiShortOn;
            _cfg.MaxTradesPerBox = MaxTradesPerBox;
```

Properties, replacing `:856-866` (keep `SessionOpenHhmm`):

```csharp
        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Session open HHMM", Order = 1, GroupName = "02. Box")]
        public int SessionOpenHhmm { get; set; }

        [NinjaScriptProperty, Range(30, 7200)]
        [Display(Name = "Box lookback (sec)", Description = "210 = 7 bars at 30s", Order = 2, GroupName = "02. Box")]
        public int BoxLookbackSec { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Box min bars", Description = "Consecutive passing bars before it seals", Order = 3, GroupName = "02. Box")]
        public int BoxMinBars { get; set; }

        [NinjaScriptProperty, Range(1.0, 99.0)]
        [Display(Name = "Box range percentile", Order = 4, GroupName = "02. Box")]
        public double BoxRangePctile { get; set; }

        [NinjaScriptProperty, Range(20, 2000)]
        [Display(Name = "Box sample ring", Order = 5, GroupName = "02. Box")]
        public int BoxSampleN { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Box mean samples", Description = "Also the cold start: nothing trades until this many boxes have sealed", Order = 6, GroupName = "02. Box")]
        public int BoxMeanSamples { get; set; }

        [NinjaScriptProperty, Range(0.05, 5.0)]
        [Display(Name = "Box valid lo (x mean)", Order = 7, GroupName = "02. Box")]
        public double BoxValidLo { get; set; }

        [NinjaScriptProperty, Range(0.5, 20.0)]
        [Display(Name = "Box valid hi (x mean)", Order = 8, GroupName = "02. Box")]
        public double BoxValidHi { get; set; }

        [NinjaScriptProperty, Range(0.05, 10.0)]
        [Display(Name = "Box dead (ATR)", Order = 9, GroupName = "02. Box")]
        public double BoxDeadAtr { get; set; }

        [NinjaScriptProperty, Range(60, 86400)]
        [Display(Name = "Box max age (sec)", Order = 10, GroupName = "02. Box")]
        public int BoxMaxAgeSec { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Box arms per edge", Order = 11, GroupName = "02. Box")]
        public int BoxArmsPerEdge { get; set; }

        [NinjaScriptProperty, Range(0, 3600)]
        [Display(Name = "Box arm cooldown (sec)", Order = 12, GroupName = "02. Box")]
        public int BoxArmCooldownSec { get; set; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && scripts/check.sh && grep -c "BbScale.Bars" ninjascript/BreakBoxStrategy.cs`
Expected: PASS — `ALL PASS (…)`, `compiles clean` from `nt8c`, and the grep counts every horizon dial that had to be converted (3 for the box, plus whatever Phase 1 and the cloud already convert)

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript tests && git commit -m "feat(strategy): §6.3 box parameter surface in SECONDS, converted in BuildConfigs

No horizon is expressed in bars on the property surface. The conversion runs
inside BuildConfigs, not once at DataLoaded, because every panel toggle rebuilds
the config and a raw one would hand the engine seconds where it expects bars.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 49: Point the shell's refusal router at the v2 box engine

**Files:**
- Modify: `ninjascript/BreakBoxStrategy.cs` — the `OnEntryRejected(BbEntryEngine, string)` and `OnTriggerExpiredFor(BbEntryEngine)` routers only
- Test: `scripts/check.sh` (no new asserts — this task is the compile-and-mirror gate)

**Interfaces:**
- Consumes: `BbEngine.OnEntryRejected(string)` and `BbEngine.OnTriggerExpired()` (Task 47); the shell's refusal plumbing — `SubmitEntry`'s `qty < 1` guard, `AgeWorkingEntry`, `CancelWorkingEntry(string, bool)`, the `OrderState.Rejected` entry arm, `_owningEngine`, `_entryFromEngine` and both routers — built whole by **Task 7 (Phase 1)**
- Produces: the BOX arm of both routers, so a refused or expired box entry reaches the engine that minted the trigger (B4) instead of dying at the `Cloud` check

> **This task does not rewrite the shell's refusal paths.** Task 7 owns every one
> of them and closed B4/B6/B7/B14 there; three phases each re-writing
> `SubmitEntry` and `CancelWorkingEntry` is how the same four lines end up with
> three different rejection strings. What Phase 1 could not finish is the far
> side of the router: Task 40 deleted the v1 `BbEngine` and its callbacks, and
> Task 47 gave the v2 engine its own. This task connects the two — and it is the
> first point in the phase where `ninjascript/` compiles again, which is why the
> gate is `check.sh` and not the test runner.

- [ ] **Step 1: Write the failing test**

No new assert. The invariant is structural — "no refusal path drops the token on the floor" — and the half this task owns is that both routers actually reach the box engine:

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -n "_engine.OnEntryRejected\|_engine.OnTriggerExpired" ninjascript/BreakBoxStrategy.cs; grep -c "_engine.OnEntryRejected\|_engine.OnTriggerExpired" ninjascript/BreakBoxStrategy.cs
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -c "_engine.OnEntryRejected\|_engine.OnTriggerExpired" ninjascript/BreakBoxStrategy.cs; scripts/check.sh 2>&1 | tail -3`
Expected: FAIL — the count is under 2, and `nt8c` is red on the router: the callbacks it named belonged to the engine Task 40 deleted. Any count below 2 means a box refusal or a box expiry stops at the `Cloud` check and the arm is never returned (B4).

- [ ] **Step 3: Write minimal implementation**

The two routers Task 7 added — give each its box arm. Nothing else in the file is touched:

```csharp
        // §4.1: expiry, disarm and rejection go ONLY to the engine that owns the
        // live trigger. Broadcasting them would restore a token in an engine that
        // never minted one — and `_entryFromEngine` (Task 7) is what keeps a
        // hand-clicked entry, which owns no token at all, out of here entirely.
        private void OnEntryRejected(BbEntryEngine engine, string reason)
        {
            if (engine == BbEntryEngine.Cloud) { if (_cloud != null) _cloud.OnEntryRejected(reason); }
            else if (_engine != null) _engine.OnEntryRejected(reason);
        }

        private void OnTriggerExpiredFor(BbEntryEngine engine)
        {
            if (engine == BbEntryEngine.Cloud) { if (_cloud != null) _cloud.OnTriggerExpired(); }
            else if (_engine != null) _engine.OnTriggerExpired();
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && scripts/check.sh && grep -c "_engine.OnEntryRejected\|_engine.OnTriggerExpired" ninjascript/BreakBoxStrategy.cs`
Expected: PASS — `ALL PASS (…)` from the runner, `compiles clean` from `nt8c`, and `2` from the grep

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript && git commit -m "fix(strategy): route box refusals and expiries to the v2 engine (B4)

Task 7 built the shell's refusal plumbing and both routers; the rewrite in this
phase replaced the engine underneath them. The routers now call the v2 engine's
own OnEntryRejected/OnTriggerExpired, so a refused box entry refunds its arm
instead of burning the box, and an expiry is mirrored idempotently rather than
raced by a second clock.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```
## Phase 4 — History persistence and the vertical panel

> **Line numbers below were read off disk on 2026-08-16, before Phases 1–3 land.** Every citation was verified against the current file; Phases 1–3 shift them. Re-grep the quoted anchor text before editing — the anchors are chosen to survive the shift, the numbers are not.

---

### Task 60: `BreakBoxHistory.cs` — the record and its wire format

**Files:**
- Create: `ninjascript/BreakBoxHistory.cs`
- Create: `tests/HistoryTests.cs`
- Modify: `tests/BreakBox.Tests.csproj:9-13` (the `ItemGroup` of `Compile Include`s)
- Modify: `tests/Program.cs:45-48` (`Main` — ONE line inserted into the run list, never a rewrite of it)
- Modify: `scripts/check.sh:28` (`FILES=(...)`)
- Test: `tests/HistoryTests.cs`

**Interfaces:**
- Consumes: nothing — this is the root of the phase.
- Produces: `public struct BbTradeRecord { DateTime Ts; int Dir; double Entry, Exit; int Qty; double R, Pnl; string Engine, ExitReason, CfgHash; }` · `public static string BbHistory.Serialise(BbTradeRecord)` · `public static bool BbHistory.TryParse(string, out BbTradeRecord)`

- [ ] **Step 1: Write the failing test**

Create `tests/HistoryTests.cs`:

```csharp
// HistoryTests — the trade journal's wire format, the equity reduction, the
// config digest and the write guard.
//
// The write guard has a test for the same reason it exists: one optimisation
// sweep appending 40,000 rows to the live curve destroys the only data the
// history feature is for, and it destroys it silently. A rule that expensive
// does not live in an impure file where nothing can assert it.
using System;
using System.Collections.Generic;
using BreakBoxCore;

public static class HistoryTests
{
    public static void Run()
    {
        RoundTrip();
    }

    private static BbTradeRecord Rec()
    {
        BbTradeRecord r = default(BbTradeRecord);
        r.Ts = new DateTime(2026, 8, 16, 14, 37, 30);
        r.Dir = -1;
        r.Entry = 29867.25;
        r.Exit = 29873.5;
        r.Qty = 3;
        r.R = -1.0;
        r.Pnl = -50.5;
        r.Engine = "Cloud";
        r.ExitReason = "BB_Stop";
        r.CfgHash = "a1b2c3d4";
        return r;
    }

    private static void RoundTrip()
    {
        T.Section("History — JSONL round trip");

        BbTradeRecord a = Rec();
        string line = BbHistory.Serialise(a);
        T.Check(line.IndexOf('\n') < 0 && line.IndexOf('\r') < 0, "one record is exactly one line");

        BbTradeRecord b;
        T.Check(BbHistory.TryParse(line, out b), "its own output parses");
        T.Check(a.Ts == b.Ts, "ts survives");
        T.CheckInt(b.Dir, -1, "dir survives");
        T.CheckClose(b.Entry, 29867.25, "entry survives");
        T.CheckClose(b.Exit, 29873.5, "exit survives");
        T.CheckInt(b.Qty, 3, "qty survives");
        T.CheckClose(b.R, -1.0, "R survives");
        // The negative case has its own assert because a loss is the row a
        // sign bug hides in: the equity curve still LOOKS like a curve.
        T.CheckClose(b.Pnl, -50.5, "a NEGATIVE pnl survives");
        T.Check(b.Engine == "Cloud", "engine survives");
        T.Check(b.ExitReason == "BB_Stop", "exit reason survives");
        T.Check(b.CfgHash == "a1b2c3d4", "config hash survives");

        // The file is a trust boundary: a human edits it, and a kill -9
        // mid-append leaves half a line. Neither may parse into a WRONG
        // record — a truncated row that silently reads pnl = 0 is worse than
        // a row that is dropped.
        BbTradeRecord junk;
        T.Check(!BbHistory.TryParse("", out junk), "empty line rejected");
        T.Check(!BbHistory.TryParse("   ", out junk), "blank line rejected");
        T.Check(!BbHistory.TryParse("{\"ts\":\"2026-08-16T14:37:30\",\"dir\":-1", out junk),
                "a truncated line is rejected, not half-read");
        T.Check(!BbHistory.TryParse("not json at all", out junk), "garbage rejected");
    }
}
```

Register it in `tests/Program.cs` by INSERTING one line after `ShellTests.Run();` — do not
rewrite the block. Every phase adds a suite here and a phase that replaces the list instead of
appending to it silently retires ~40 asserts from the other phases with nothing red to show for it:

```csharp
        BoxTests.Run();
        BracketTests.Run();
        ShellTests.Run();
        HistoryTests.Run();     // <- the only new line
```

Add the file to `tests/BreakBox.Tests.csproj`, after the `BreakBoxExits.cs` line:

```xml
    <Compile Include="../ninjascript/BreakBoxHistory.cs" Condition="Exists('../ninjascript/BreakBoxHistory.cs')" />
```

And to `scripts/check.sh` so the NT8 compilation unit carries it too:

```bash
FILES=(BreakBoxTypes BreakBoxCore BreakBoxExits BreakBoxHistory BreakBoxStrategy BreakBoxPanel)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error CS|FAIL|ALL PASS"`
Expected: FAIL with `error CS0103: The name 'BbHistory' does not exist in the current context`

- [ ] **Step 3: Write minimal implementation**

Create `ninjascript/BreakBoxHistory.cs`:

```csharp
// BreakBoxHistory.cs — the trade journal: one record per closed trade, its
// hand-rolled JSONL line, the equity reduction the panel plots, the digest that
// tells two configurations apart, and the guard that decides whether a row may
// be written at all.
//
// ZERO `using NinjaTrader.*`, own namespace `BreakBoxCore`, C# 7.3 only — same
// rules and same reason as BreakBoxTypes.cs.
//
// HAND-ROLLED ON PURPOSE. No System.Text.Json: NT8 is .NET Framework 4.8 and
// the test runner is net8, and no serializer sits on both reference paths. One
// that compiled here and not there would push this file out of the pure set,
// and the pure set is the only thing the assert suite can see.
//
// NO FILE I/O HERE. File.AppendAllText / File.ReadAllLines live in
// BreakBoxStrategy.cs. This file decides WHAT to write and WHETHER to write it;
// the shell does the writing. That split is what makes the write guard — the
// one rule standing between an optimisation sweep and the live equity curve —
// an assert in tests/ rather than a hope.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BreakBoxCore
{
    public struct BbTradeRecord
    {
        public DateTime Ts;
        public int Dir;                 // +1 long, -1 short
        public double Entry, Exit;
        public int Qty;
        public double R;                // realised, in R multiples of the initial risk
        public double Pnl;              // currency, gross — the same basis the HUD reports
        public string Engine;           // "Cloud" / "Break": which engine owned the trigger
        public string ExitReason;
        public string CfgHash;
    }

    public static class BbHistory
    {
        // Every field is required. A record missing one is a torn write, and a
        // torn write that parses is a fabricated trade in the equity curve.
        private const int RequiredMask = 0x3FF;

        public static string Serialise(BbTradeRecord r)
        {
            StringBuilder sb = new StringBuilder(220);
            sb.Append("{\"ts\":\"").Append(r.Ts.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            sb.Append("\",\"dir\":").Append(r.Dir.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"entry\":").Append(Num(r.Entry));
            sb.Append(",\"exit\":").Append(Num(r.Exit));
            sb.Append(",\"qty\":").Append(r.Qty.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"r\":").Append(Num(r.R));
            sb.Append(",\"pnl\":").Append(Num(r.Pnl));
            sb.Append(",\"engine\":\"").Append(Clean(r.Engine));
            sb.Append("\",\"exitReason\":\"").Append(Clean(r.ExitReason));
            sb.Append("\",\"cfgHash\":\"").Append(Clean(r.CfgHash)).Append("\"}");
            return sb.ToString();
        }

        public static bool TryParse(string line, out BbTradeRecord r)
        {
            r = default(BbTradeRecord);
            if (string.IsNullOrEmpty(line))
                return false;
            string s = line.Trim();
            if (s.Length < 2 || s[0] != '{' || s[s.Length - 1] != '}')
                return false;
            s = s.Substring(1, s.Length - 2);

            // Splitting on ',' is safe only because Clean() strips commas out of
            // every string field on the way in and the numbers are invariant.
            // That is the whole reason the writer sanitises instead of escaping:
            // there is no escape grammar to get wrong on the way back.
            string[] parts = s.Split(',');
            int seen = 0;
            double d;
            for (int i = 0; i < parts.Length; i++)
            {
                int c = parts[i].IndexOf(':');
                if (c < 0)
                    return false;
                string k = Unquote(parts[i].Substring(0, c).Trim());
                string v = parts[i].Substring(c + 1).Trim();

                if (k == "ts")
                {
                    DateTime t;
                    if (!DateTime.TryParseExact(Unquote(v), "yyyy-MM-ddTHH:mm:ss",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                        return false;
                    r.Ts = t; seen |= 1 << 0;
                }
                else if (k == "dir") { if (!Dbl(v, out d)) return false; r.Dir = (int)d; seen |= 1 << 1; }
                else if (k == "entry") { if (!Dbl(v, out d)) return false; r.Entry = d; seen |= 1 << 2; }
                else if (k == "exit") { if (!Dbl(v, out d)) return false; r.Exit = d; seen |= 1 << 3; }
                else if (k == "qty") { if (!Dbl(v, out d)) return false; r.Qty = (int)d; seen |= 1 << 4; }
                else if (k == "r") { if (!Dbl(v, out d)) return false; r.R = d; seen |= 1 << 5; }
                else if (k == "pnl") { if (!Dbl(v, out d)) return false; r.Pnl = d; seen |= 1 << 6; }
                else if (k == "engine") { r.Engine = Unquote(v); seen |= 1 << 7; }
                else if (k == "exitReason") { r.ExitReason = Unquote(v); seen |= 1 << 8; }
                else if (k == "cfgHash") { r.CfgHash = Unquote(v); seen |= 1 << 9; }
            }
            return seen == RequiredMask;
        }

        // "R" is the round-trip format: parse(format(x)) == x exactly. "0.00"
        // would quietly re-quantise every price the panel later subtracts.
        private static string Num(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool Dbl(string v, out double d)
        {
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        private static string Unquote(string v)
        {
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                return v.Substring(1, v.Length - 2);
            return v;
        }

        // Our own strings only ever hold [A-Za-z0-9_:] — but this file is read
        // back from disk, where a human can edit it. One stray quote or comma
        // would otherwise produce a line that parses into a WRONG record
        // instead of failing.
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                bool bad = ch == '"' || ch == '\\' || ch == ',' || ch == '{' || ch == '}' || ch == ':' || ch < ' ';
                sb.Append(bad ? '_' : ch);
            }
            return sb.ToString();
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS — `ALL PASS (n checks)`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxHistory.cs tests/HistoryTests.cs tests/Program.cs tests/BreakBox.Tests.csproj scripts/check.sh && \
git commit -m "feat(history): BbTradeRecord + hand-rolled JSONL round trip

No System.Text.Json: NT8 is net48, the runner is net8, and no serializer
sits on both reference paths. Sanitise-on-write instead of an escape
grammar, and a required-field mask so a torn append is dropped rather
than half-read into a fabricated trade.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 61: Cumulative equity, the canonical config string, and the hash

**Files:**
- Modify: `ninjascript/BreakBoxHistory.cs` (append to `BbHistory`)
- Test: `tests/HistoryTests.cs`

**Interfaces:**
- Consumes: `BbTradeRecord` (Task 60)
- Produces: `public static double[] BbHistory.CumulativeEquity(IReadOnlyList<BbTradeRecord>)` · `public static string BbHistory.Hash(string)` · **plus one name not in the shared contract:** `public static string BbHistory.Canonical(IReadOnlyList<string> pairs)` — the `key=value` list the shell hands in, with the excluded keys dropped **here**, in the pure file. It is added because "`_uiRiskMult` is excluded" (§10) is a rule that only earns its keep if it can be asserted, and the call site is impure.

- [ ] **Step 1: Write the failing test**

Add to `tests/HistoryTests.cs` — one new method, and its call in `Run()`:

```csharp
    public static void Run()
    {
        RoundTrip();
        EquityAndHash();
    }

    private static void EquityAndHash()
    {
        T.Section("History — cumulative equity and the config digest");

        List<BbTradeRecord> rows = new List<BbTradeRecord>();
        T.CheckInt(BbHistory.CumulativeEquity(rows).Length, 0, "an empty file yields an empty curve");
        T.CheckInt(BbHistory.CumulativeEquity(null).Length, 0, "a null list does not throw");

        double[] pnls = { 100.0, -50.5, 25.0 };
        for (int i = 0; i < pnls.Length; i++)
        {
            BbTradeRecord r = Rec();
            r.Pnl = pnls[i];
            rows.Add(r);
        }
        double[] cum = BbHistory.CumulativeEquity(rows);
        T.CheckInt(cum.Length, 3, "one point per trade");
        T.CheckClose(cum[0], 100.0, "cum after trade 1");
        T.CheckClose(cum[1], 49.5, "cum after the loss");
        T.CheckClose(cum[2], 74.5, "cum after trade 3");

        // The digest is what draws the seam between configurations. If it moved
        // when the user clicked Risk 1.5x, every touch of the size dial would
        // dim the whole history and the seam would mean nothing.
        string a = BbHistory.Canonical(new List<string> { "cloud=1", "box=0", "risk=1" });
        string b = BbHistory.Canonical(new List<string> { "cloud=1", "box=0", "risk=1.5" });
        T.Check(a == b, "risk is excluded from the canonical string");
        T.Check(BbHistory.Hash(a) == BbHistory.Hash(b), "and therefore from the hash");

        // Order-independent: BuildConfigs may append in any order it likes.
        string c = BbHistory.Canonical(new List<string> { "box=0", "cloud=1" });
        T.Check(a == c, "the canonical string is order-independent");

        string d = BbHistory.Canonical(new List<string> { "cloud=0", "box=0" });
        T.Check(BbHistory.Hash(a) != BbHistory.Hash(d), "a real config change DOES move the hash");
        T.CheckInt(BbHistory.Hash(a).Length, 8, "8 hex chars, sized to fit a panel row");
        T.Check(BbHistory.Hash(a) == BbHistory.Hash(a), "the hash is stable across calls");
        T.CheckInt(BbHistory.Hash(null).Length, 8, "a null config hashes rather than throwing");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error CS|FAIL|ALL PASS"`
Expected: FAIL with `error CS0117: 'BbHistory' does not contain a definition for 'CumulativeEquity'`

- [ ] **Step 3: Write minimal implementation**

Append inside `BbHistory` in `ninjascript/BreakBoxHistory.cs`, before the private helpers:

```csharp
        // Running sum, one point per trade. Not a rolling window and not
        // resampled by time: the x axis is TRADES, so a quiet day does not
        // stretch the curve and a busy one does not compress it.
        public static double[] CumulativeEquity(IReadOnlyList<BbTradeRecord> rows)
        {
            if (rows == null || rows.Count == 0)
                return new double[0];
            double[] cum = new double[rows.Count];
            double run = 0.0;
            for (int i = 0; i < rows.Count; i++)
            {
                run += rows[i].Pnl;
                cum[i] = run;
            }
            return cum;
        }

        // Keys that scale SIZE rather than change the DECISION. The shell hands
        // in everything it has, including risk, and the drop happens here — in
        // the file the assert suite can see — rather than at the impure call
        // site. §10: including `_uiRiskMult` would fragment the curve into a
        // new colour every time the user touches the Risk buttons, which
        // defeats the entire point of the seam.
        private static readonly string[] Excluded = { "risk" };

        public static string Canonical(IReadOnlyList<string> pairs)
        {
            if (pairs == null || pairs.Count == 0)
                return "";
            List<string> keep = new List<string>(pairs.Count);
            for (int i = 0; i < pairs.Count; i++)
            {
                string p = pairs[i];
                if (string.IsNullOrEmpty(p))
                    continue;
                int eq = p.IndexOf('=');
                string key = eq < 0 ? p : p.Substring(0, eq);
                bool drop = false;
                for (int k = 0; k < Excluded.Length; k++)
                    if (string.Equals(key, Excluded[k], StringComparison.Ordinal))
                        drop = true;
                if (!drop)
                    keep.Add(p);
            }
            // Sorted, so a reordering of BuildConfigs' own statements does not
            // read as a configuration change and dim every historical trade.
            keep.Sort(StringComparer.Ordinal);
            return string.Join(";", keep.ToArray());
        }

        // SHA-1, first 8 hex chars. A fingerprint, not a security primitive:
        // 4 billion buckets against a few dozen configurations a year, and it
        // has to fit in a 300px panel row next to the trade.
        public static string Hash(string canonicalConfig)
        {
            string s = canonicalConfig == null ? "" : canonicalConfig;
            using (System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                StringBuilder sb = new StringBuilder(8);
                for (int i = 0; i < 4; i++)
                    sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxHistory.cs tests/HistoryTests.cs && \
git commit -m "feat(history): cumulative equity + config digest, risk excluded

The exclusion of _uiRiskMult lives in the pure file so it can be an
assert: it scales size, not the decision, and a hash that tracked it
would dim the whole curve every time the user clicks 1.5x.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 62: The write guard — the rule that protects the curve from a sweep

**Files:**
- Modify: `ninjascript/BreakBoxHistory.cs` (append to `BbHistory`)
- Test: `tests/HistoryTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `public static bool BbHistory.ShouldWrite(bool realtime, bool tickReplay, string account)` · `public static string BbHistory.FileName(string instrument, string account)`

- [ ] **Step 1: Write the failing test**

Add to `tests/HistoryTests.cs`, plus its call in `Run()`:

```csharp
    public static void Run()
    {
        RoundTrip();
        EquityAndHash();
        WriteGuard();
    }

    private static void WriteGuard()
    {
        T.Section("History — the write guard");

        // The fill path also runs in the Strategy Analyzer, in optimisation
        // sweeps, and over historical bars at startup. One 2,000-iteration
        // sweep would append tens of thousands of junk rows to the live curve —
        // the feature destroying its own data set. This is the assert that
        // stands between those two things.
        T.Check(BbHistory.ShouldWrite(true, false, "Sim101"), "a live realtime account writes");
        T.Check(!BbHistory.ShouldWrite(false, false, "Sim101"), "historical bars do NOT write");
        T.Check(!BbHistory.ShouldWrite(true, true, "Sim101"), "TickReplay re-runs the fill path: no write");
        T.Check(!BbHistory.ShouldWrite(true, false, "Backtest"), "the Strategy Analyzer account is skipped");
        T.Check(!BbHistory.ShouldWrite(true, false, "backtest_4"), "and its numbered variants, case-insensitively");
        T.Check(!BbHistory.ShouldWrite(true, false, ""), "an unresolved account does not write");
        T.Check(!BbHistory.ShouldWrite(true, false, null), "and neither does a null one");

        // Replay fills are real fills against recorded tape and worth keeping —
        // but mixed into the live file, a replayed October reads as this week's
        // P&L. Separate file, same format.
        T.Check(BbHistory.ShouldWrite(true, false, "Playback101"), "Replay writes");
        T.Check(BbHistory.FileName("MNQ 09-26", "Playback101").EndsWith("-replay.jsonl",
                StringComparison.Ordinal), "...to a -replay file");
        T.Check(!BbHistory.FileName("MNQ 09-26", "Sim101").Contains("-replay"),
                "the live file has no suffix");
        T.Check(BbHistory.FileName("MNQ 09-26", "Sim101") == "history-MNQ_09-26-Sim101.jsonl",
                "the file name is one instrument, one account");
        T.Check(BbHistory.FileName(null, null).Length > 0, "nulls yield a name, not an exception");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error CS|FAIL|ALL PASS"`
Expected: FAIL with `error CS0117: 'BbHistory' does not contain a definition for 'ShouldWrite'`

- [ ] **Step 3: Write minimal implementation**

Append inside `BbHistory` in `ninjascript/BreakBoxHistory.cs`:

```csharp
        // THE WRITE GUARD (§10). Every argument is an NT8 fact passed IN:
        // `realtime` is State == State.Realtime, `tickReplay` is
        // Bars.IsTickReplay, `account` is Account.Name. They are parameters
        // rather than reads because this decision is the most expensive one in
        // the file and it has to be assertable.
        //
        // The account check is not redundant with the state check: a Strategy
        // Analyzer iteration reaches State.Realtime on some NT8 builds, and
        // that is exactly the path that would empty 40,000 rows into the live
        // curve before anyone noticed.
        public static bool ShouldWrite(bool realtime, bool tickReplay, string account)
        {
            if (!realtime || tickReplay)
                return false;
            if (string.IsNullOrEmpty(account))
                return false;
            return !account.StartsWith("Backtest", StringComparison.OrdinalIgnoreCase);
        }

        // One file per instrument per account, and Replay gets its own. NT8
        // names the Market Replay account "Playback101"; matching on the prefix
        // rather than the exact name survives NT8 numbering a second one.
        public static string FileName(string instrument, string account)
        {
            string ins = Clean(instrument == null ? "unknown" : instrument).Replace(' ', '_');
            string acc = Clean(account == null ? "unknown" : account).Replace(' ', '_');
            if (ins.Length == 0) ins = "unknown";
            if (acc.Length == 0) acc = "unknown";
            bool replay = acc.StartsWith("Playback", StringComparison.OrdinalIgnoreCase);
            return "history-" + ins + "-" + acc + (replay ? "-replay" : "") + ".jsonl";
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxHistory.cs tests/HistoryTests.cs && \
git commit -m "feat(history): the write guard, with the assert that justifies it

Realtime only, never TickReplay, never a Backtest* account, and Replay
to its own -replay file. Without it one optimisation sweep appends tens
of thousands of junk rows to the live equity curve.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 63: The file I/O and the journal call site (shell)

**Files:**
- Modify: `ninjascript/BreakBoxStrategy.cs` — the `Fields` region (`:91-144`), `BuildConfigs()` (`:272-322`), `State.DataLoaded` (`:239-258`), `WentFlat` (`:633-651`), `OnExecutionUpdate`'s flat branch (`:700-702`)
- Test: `scripts/check.sh` (the NT8 compilation unit — file I/O has no reach in the assert suite by construction)

**Interfaces:**
- Consumes: `BbHistory.ShouldWrite` / `FileName` / `Serialise` / `TryParse` / `Canonical` / `Hash` (Tasks 60–62) · from Phase 3's shell: `private BbEntryEngine _owningEngine;`, `private bool _uiBreakOn, _uiLongOn, _uiShortOn;` and `private bool _uiCloudOn;`, `private int BarSeconds();` — the box engine's toggle field is `_uiBreakOn` (Phase 3 names it after `EnableBreak`); the panel button on top of it is labelled "Box"
- Produces: `private readonly List<BbTradeRecord> _history` (newest last) · `private string _cfgHash` · `private void OpenHistory()` · `private void AppendHistory(BbTradeRecord)` — the panel reads all three.

- [ ] **Step 1: Write the failing test**

The check here is the compile gate: add the call sites first, so `check.sh` fails on the missing methods. In `ninjascript/BreakBoxStrategy.cs`, `State.DataLoaded` — after `_swings = new SwingDetector(SwingStrength);`:

```csharp
                OpenHistory();
```

And in `WentFlat`, immediately after the `pnl` computation (`:635-636`):

```csharp
            // Journalled BEFORE the bracket is torn down: `_bracket.Dir` is
            // zeroed twelve lines below, and reading it after is how a history
            // file fills up with dir=0 rows that plot but mean nothing.
            BbTradeRecord rec = default(BbTradeRecord);
            rec.Ts = Time[0];
            rec.Dir = _bracket.Dir;
            rec.Entry = _bracket.EntryPx;
            rec.Exit = exitPx;
            rec.Qty = _bracket.QtyTotal;
            rec.R = _bracket.R > 0.0 ? (exitPx - _bracket.EntryPx) * _bracket.Dir / _bracket.R : 0.0;
            rec.Pnl = pnl;
            rec.Engine = _owningEngine.ToString();
            rec.ExitReason = _exitReason.Length > 0 ? _exitReason : "unknown";
            rec.CfgHash = _cfgHash;
            AppendHistory(rec);
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | grep -E "error CS|compiles clean"`
Expected: FAIL with `error CS0103: The name 'OpenHistory' does not exist in the current context` (and the same for `AppendHistory`, `_cfgHash`)

- [ ] **Step 3: Write minimal implementation**

Add to the `Fields` region of `ninjascript/BreakBoxStrategy.cs`, after the governor block (`:129`):

```csharp
        // History (§10). The list is the panel's data source and holds EVERY
        // trade of this run, written or not: in a backtest you still want to
        // see the curve the run produced — you just must not let it touch the
        // live file. The guard is about the FILE, not about the chart.
        private readonly List<BbTradeRecord> _history = new List<BbTradeRecord>();
        private string _histPath = "";
        private string _cfgHash = "";
```

Add a `#region History` before `#region Governor` (`:754`):

```csharp
        #region History (file I/O — the impure half of BreakBoxHistory.cs)

        // Resolved once, at DataLoaded. NinjaTrader.Core.Globals.UserDataDir is
        // a directory probe, and Account.Name is stable for the strategy's life.
        private void OpenHistory()
        {
            string acct = Account != null ? Account.Name : "";
            string ins = Instrument != null ? Instrument.FullName : "";
            string dir = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "BreakBox");
            _histPath = System.IO.Path.Combine(dir, BbHistory.FileName(ins, acct));
            _history.Clear();

            try
            {
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                if (!System.IO.File.Exists(_histPath))
                    return;
                string[] lines = System.IO.File.ReadAllLines(_histPath);
                // Skip, never throw. A half-written last line — the platform was
                // killed mid-append — must cost that ONE trade, not the chart.
                for (int i = 0; i < lines.Length; i++)
                {
                    BbTradeRecord r;
                    if (BbHistory.TryParse(lines[i], out r))
                        _history.Add(r);
                }
                Print("BreakBox: history loaded, " + _history.Count + " trades from " + _histPath);
            }
            catch (Exception ex)
            {
                // A strategy that refuses to start because a log file is locked
                // is a worse outcome than a strategy with an empty chart.
                Print("BreakBox: history unreadable (" + ex.Message + ")");
            }
        }

        private void AppendHistory(BbTradeRecord r)
        {
            _history.Add(r);

            if (!BbHistory.ShouldWrite(State == State.Realtime,
                                       Bars != null && Bars.IsTickReplay,
                                       Account != null ? Account.Name : ""))
                return;

            try
            {
                System.IO.File.AppendAllText(_histPath, BbHistory.Serialise(r) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Print("BreakBox: history NOT written (" + ex.Message + ")");
            }
        }

        #endregion
```

At the end of `BuildConfigs()`, before the `if (_engine != null)` handover (`:318-321`):

```csharp
            // The digest that draws the seam between configurations. Rebuilt
            // HERE rather than at DataLoaded because BuildConfigs is exactly
            // what a panel toggle calls: a hash that only tracked the startup
            // parameters would stamp post-toggle trades as identical to
            // pre-toggle ones, which is the contamination the seam exists to
            // make visible. `risk` is listed and then dropped by Canonical —
            // listed so the exclusion is legible at the call site too.
            _cfgHash = BbHistory.Hash(BbHistory.Canonical(new List<string>
            {
                "risk=" + _uiRiskMult.ToString("0.##", CultureInfo.InvariantCulture),
                "cloud=" + (_uiCloudOn ? "1" : "0"),
                "box=" + (_uiBreakOn ? "1" : "0"),
                "long=" + (_uiLongOn ? "1" : "0"),
                "short=" + (_uiShortOn ? "1" : "0"),
                "stop=" + _uiStopSource,
                "sbuf=" + StopBufferTicks.ToString(CultureInfo.InvariantCulture),
                "smin=" + StopMinAtr.ToString("0.###", CultureInfo.InvariantCulture),
                "smax=" + StopMaxAtr.ToString("0.###", CultureInfo.InvariantCulture),
                "tiers=" + TierCount.ToString(CultureInfo.InvariantCulture),
                "tp1r=" + Tp1R.ToString("0.###", CultureInfo.InvariantCulture),
                "tp2r=" + Tp2R.ToString("0.###", CultureInfo.InvariantCulture),
                "tp3r=" + Tp3R.ToString("0.###", CultureInfo.InvariantCulture),
                "tp1pct=" + Tp1Pct.ToString(CultureInfo.InvariantCulture),
                "be=" + (BreakevenOnTp1 ? "1" : "0"),
                "bar=" + BarSeconds().ToString(CultureInfo.InvariantCulture)
            }));
```

In `OnExecutionUpdate`, replace the flat branch (`:700-702`):

```csharp
            // Any exit that leaves us flat closes the trade out.
            if (_inTrade && Position.MarketPosition == MarketPosition.Flat)
            {
                // The exit order's own signal name is the only honest reason
                // available here — BB_Stop / BB_TP2 / BB_Flatten. FlattenAll
                // already set a richer one, so it wins.
                if (_exitReason.Length == 0)
                    _exitReason = sig;
                WentFlat(price);
            }
```

And at the end of `WentFlat`, before `CheckDailyLimits();`:

```csharp
            // Cleared here, not at the next entry: a stale reason on the next
            // trade's record is indistinguishable from a real one.
            _exitReason = "";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -5`
Expected: PASS — `ALL PASS (n checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxStrategy.cs && \
git commit -m "feat(history): journal every closed trade from the shell

Record built before the bracket is torn down (Dir is zeroed below it),
exit reason taken from the exit order's signal name and cleared after
use, config hash rebuilt inside BuildConfigs so a panel toggle moves the
seam. Reads skip unparseable lines instead of refusing to start.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 64: Panel chrome — 300 DIP, docked left, header / scroll / action bar

**Files:**
- Modify: `ninjascript/BreakBoxPanel.cs:38-53` (fields), `:57-200` (`BuildPanel`), `:202-212` (`DisposePanel`), `:216-283` (widgets)
- Test: `scripts/check.sh`

**Interfaces:**
- Consumes: from Phase 3's shell — `private int BarSeconds();`
- Produces: `private DockPanel _panelRoot` · `private StackPanel _body` · `private static Grid Row2(UIElement, UIElement)` · `private static Grid Cols(params UIElement[])` · `private static TextBlock Section(string)` · `private static TextBlock Small(string)` · brushes `HeaderBg / DimBrush / OkBrush / WarnBrush / LossBrush / RuleBrush`

- [ ] **Step 1: Write the failing test**

Replace `BuildPanel` (`:57-200`) and `DisposePanel` (`:202-212`) in `ninjascript/BreakBoxPanel.cs`:

```csharp
        private void BuildPanel()
        {
            if (!ShowPanel || ChartControl == null)
                return;

            // Read off the STRATEGY thread and capture: Instrument and
            // BarSeconds() belong to NinjaScript, and reaching for them from
            // inside the dispatcher lambda is a cross-thread read that works
            // right up until it does not.
            string instLabel = (Instrument != null ? Instrument.FullName : "--")
                             + "  ·  " + BarSeconds() + "s";

            ChartControl.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (_panelRoot != null && UserControlCollection.Contains(_panelRoot))
                    return;

                _panelRoot = new DockPanel
                {
                    Width = PanelWidth,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    LastChildFill = true,
                    Background = PanelBg
                };

                // Header and action bar dock FIRST, the ScrollViewer last. In a
                // DockPanel the last child fills what is left, and that is the
                // only arrangement in which a long gate ladder scrolls instead
                // of pushing FLATTEN off the bottom of the chart.
                UIElement header = BuildHeader(instLabel);
                DockPanel.SetDock(header, Dock.Top);
                _panelRoot.Children.Add(header);

                UIElement actions = BuildActionBar();
                DockPanel.SetDock(actions, Dock.Bottom);
                _panelRoot.Children.Add(actions);

                _body = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };

                ScrollViewer scroll = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = _body
                };
                _panelRoot.Children.Add(scroll);

                UserControlCollection.Add(_panelRoot);
            }));
        }

        private void DisposePanel()
        {
            if (ChartControl == null || _panelRoot == null)
                return;
            ChartControl.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (_panelRoot != null && UserControlCollection.Contains(_panelRoot))
                    UserControlCollection.Remove(_panelRoot);
                _panelRoot = null;
            }));
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | grep -E "error CS|compiles clean"`
Expected: FAIL with `error CS0103: The name 'PanelWidth' does not exist in the current context` (and the same for `BuildHeader`, `BuildActionBar`)

- [ ] **Step 3: Write minimal implementation**

Replace the `Panel fields` region (`:38-53`) of `ninjascript/BreakBoxPanel.cs`:

```csharp
        #region Panel fields

        // 300 DIP, docked left, full height. v1 was an auto-sized Grid of
        // horizontal StackPanels: width was max(row), height was sum(row), and
        // the result was an 830x480 landscape slab where no two rows lined up.
        // The fixed width is what makes "every row is the same 2-column grid"
        // mean anything.
        private const double PanelWidth = 300;

        private DockPanel _panelRoot;
        private StackPanel _body;
        private TextBlock _statusDot, _statusText, _instText;
        private Button _autoBtn, _lockBtn;

        private static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0xFF));
        private static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x40));
        private static readonly Brush TextBrush = Brushes.White;
        private static readonly Brush PanelBg = new SolidColorBrush(Color.FromArgb(0xE8, 0x0F, 0x13, 0x1A));
        private static readonly Brush HeaderBg = new SolidColorBrush(Color.FromArgb(0xFF, 0x08, 0x0B, 0x10));
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x7E));
        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8C));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        private static readonly Brush LossBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x53, 0x4F));
        private static readonly Brush RuleBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x22, 0x2C));

        // Frozen because the static initialiser runs on whichever thread
        // touches the class first — normally NinjaScript's — and these are then
        // assigned to Foreground on the WPF thread. An unfrozen Freezable used
        // across dispatchers throws "The calling thread cannot access this
        // object", and it throws intermittently, which is the worst way to find
        // out about it.
        static BreakBoxStrategy()
        {
            Brush[] all = { OnBrush, OffBrush, PanelBg, HeaderBg, DimBrush, OkBrush, WarnBrush, LossBrush, RuleBrush };
            for (int i = 0; i < all.Length; i++)
                all[i].Freeze();
        }

        #endregion
```

Replace the `Widgets` region (`:216-283`) — `Row()` goes, the grid helpers arrive:

```csharp
        #region Widgets

        // EVERY row in the body is this: label left on a star column, value
        // right on an auto column. v1's rows were horizontal StackPanels, so
        // each one was as wide as its own content and nothing lined up with
        // anything — that is the whole of the "messy rectangle".
        private static Grid Row2(UIElement left, UIElement right)
        {
            Grid g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (left != null) { Grid.SetColumn(left, 0); g.Children.Add(left); }
            if (right != null) { Grid.SetColumn(right, 1); g.Children.Add(right); }
            return g;
        }

        // Equal-width columns, for the button strips. The action bar is the one
        // place where the 2-column rule would look wrong: three equal buttons
        // beat two wide ones and a stub.
        private static Grid Cols(params UIElement[] cells)
        {
            Grid g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            for (int i = 0; i < cells.Length; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (cells[i] == null)
                    continue;
                Grid.SetColumn(cells[i], i);
                g.Children.Add(cells[i]);
            }
            return g;
        }

        private static TextBlock Section(string title)
        {
            return new TextBlock
            {
                Text = title,
                Foreground = DimBrush,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 3)
            };
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = TextBrush,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static TextBlock Small(string text)
        {
            TextBlock t = Label(text);
            t.Foreground = DimBrush;
            t.FontSize = 10;
            return t;
        }

        private static Border Rule()
        {
            return new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 6, 0, 0) };
        }

        private static Button Toggle(string text, bool on, System.Windows.RoutedEventHandler onClick)
        {
            Button b = new Button
            {
                Content = text,
                Margin = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
                FontSize = 10,
                Foreground = TextBrush,
                Background = on ? OnBrush : OffBrush,
                BorderThickness = new Thickness(0)
            };
            b.Click += onClick;
            return b;
        }

        private static Button Action_(string text, System.Windows.RoutedEventHandler onClick)
        {
            Button b = new Button
            {
                Content = text,
                Margin = new Thickness(1),
                Padding = new Thickness(4, 5, 4, 5),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                Background = Brushes.Gainsboro,
                BorderThickness = new Thickness(0)
            };
            b.Click += onClick;
            return b;
        }

        private static void Paint(Button b, bool on)
        {
            if (b != null)
                b.Background = on ? OnBrush : OffBrush;
        }

        #endregion

        #region Chrome

        private UIElement BuildHeader(string instLabel)
        {
            Border b = new Border { Background = HeaderBg, Padding = new Thickness(10, 8, 10, 8) };
            StackPanel s = new StackPanel();

            TextBlock title = new TextBlock
            {
                Text = "BREAKBOX",
                Foreground = TextBrush,
                FontSize = 13,
                FontWeight = FontWeights.Bold
            };
            _instText = Small(instLabel);
            s.Children.Add(Row2(title, _instText));

            StackPanel st = new StackPanel { Orientation = Orientation.Horizontal };
            // A text bullet, not an Ellipse: NinjaTrader.NinjaScript.DrawingTools
            // also declares Ellipse, and check.sh hoists every file's usings into
            // ONE compilation unit — so the WPF shape and the drawing tool become
            // an ambiguous reference (CS0104) in the combined build only.
            _statusDot = new TextBlock
            {
                Text = "\u25CF",
                Foreground = DimBrush,
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _statusText = Label("WARMING");
            st.Children.Add(_statusDot);
            st.Children.Add(_statusText);

            _autoBtn = Toggle("AUTO-TRADE", _uiAutoTrade, delegate
            {
                _uiAutoTrade = !_uiAutoTrade;
                Paint(_autoBtn, _uiAutoTrade);
            });
            s.Children.Add(Row2(st, _autoBtn));

            b.Child = s;
            return b;
        }

        private UIElement BuildActionBar()
        {
            Border b = new Border { Background = HeaderBg, Padding = new Thickness(9, 6, 9, 9) };
            StackPanel s = new StackPanel();

            _lockBtn = Toggle("LOCK OUT", _lockout, delegate { Dispatch(o => PanelToggleLockout()); });
            _lockBtn.Padding = new Thickness(4, 5, 4, 5);
            _lockBtn.FontWeight = FontWeights.Bold;

            s.Children.Add(Cols(
                Action_("FLATTEN", delegate { Dispatch(o => FlattenAll("panel")); }),
                Action_("BE", delegate { Dispatch(o => PanelBreakeven()); }),
                _lockBtn));
            s.Children.Add(Cols(
                Action_("MANUAL BUY", delegate { Dispatch(o => PanelManualEntry(+1)); }),
                Action_("MANUAL SELL", delegate { Dispatch(o => PanelManualEntry(-1)); })));

            b.Child = s;
            return b;
        }

        #endregion
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -5`
Expected: PASS — `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxPanel.cs && \
git commit -m "refactor(panel): 300 DIP DockPanel docked left, fixed header + action bar

Header and action bar dock first so the ScrollViewer fills the rest and
a long gate ladder scrolls instead of pushing FLATTEN off the chart.
Row2/Cols replace the horizontal StackPanels that made width = max(row).
Brushes frozen: the static init runs on NinjaScript's thread and the
assignment happens on WPF's.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 65: The gate ladder — WHY NO TRADE

**Files:**
- Modify: `ninjascript/BreakBoxTypes.cs` — append one static to `BbGateReport` (the class is Phase 1's; no verified line number)
- Modify: `ninjascript/BreakBoxPanel.cs` — `BuildPanel`'s body list, `#region Chrome`
- Test: `tests/HistoryTests.cs`

**Interfaces:**
- Consumes: `BbGateReport { string Block; string BlockDetail; int GateDepth; }` (Phase 1) · `BbCloudState.Gate`, `BbEngineState.Gate` (Phases 2–3)
- Produces: `public static int BbGateReport.RowState(int row, int depth)` — 0 passed, 1 blocker, 2 not evaluated · `private const int GateRows = 10` (the depth of the DEEPER of the two ladders — the box's) · `private static readonly string[] CloudGates / BoxGates` · `private readonly TextBlock[] _gateMark/_gateName/_gateVal`

- [ ] **Step 1: Write the failing test**

Add to `tests/HistoryTests.cs`, plus its call in `Run()`:

```csharp
    public static void Run()
    {
        RoundTrip();
        EquityAndHash();
        WriteGuard();
        GateLadder();
    }

    private static void GateLadder()
    {
        T.Section("Panel — gate ladder row states");

        // The defect this exists to make impossible: v1's panel printed READY
        // while printing "(out of band)" two rows below and never related the
        // two. A gate AFTER the blocker was never evaluated, so rendering it as
        // OK is a lie and rendering it as FAILED is a different lie.
        T.CheckInt(BbGateReport.RowState(0, 3), 0, "a gate before the blocker passed");
        T.CheckInt(BbGateReport.RowState(2, 3), 0, "and the one right before it");
        T.CheckInt(BbGateReport.RowState(3, 3), 1, "the blocker itself");
        T.CheckInt(BbGateReport.RowState(4, 3), 2, "everything after it was NOT evaluated");

        // depth = -1 is "nothing blocks": every row passed, none is dimmed.
        T.CheckInt(BbGateReport.RowState(0, -1), 0, "nothing blocks: row 0 passed");
        T.CheckInt(BbGateReport.RowState(5, -1), 0, "nothing blocks: the last row passed too");

        // Warmup blocks at the first gate, which must not read as "all dimmed".
        T.CheckInt(BbGateReport.RowState(0, 0), 1, "a warmup block is the blocker, not a dimmed row");
        T.CheckInt(BbGateReport.RowState(1, 0), 2, "and everything below it is dimmed");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error CS|FAIL|ALL PASS"`
Expected: FAIL with `error CS0117: 'BbGateReport' does not contain a definition for 'RowState'`

- [ ] **Step 3: Write minimal implementation**

Append inside `BbGateReport` in `ninjascript/BreakBoxTypes.cs`:

```csharp
        // How the panel should render ladder row `row` given `depth`, the index
        // of the first failing gate (-1 = nothing blocks). 0 = passed,
        // 1 = the blocker, 2 = never evaluated.
        //
        // Pure and here rather than in the panel because "everything after the
        // blocker is DIMMED, not FAILED" is the entire point of the ladder: the
        // engine short-circuits at the first failure, so the rows below it were
        // never computed and any verdict on them is invented.
        public static int RowState(int row, int depth)
        {
            if (depth < 0)
                return 0;
            return row < depth ? 0 : (row == depth ? 1 : 2);
        }
```

In `ninjascript/BreakBoxPanel.cs`, add to the fields region:

```csharp
        // TEN rendered rows, because the DEEPER of the two ladders — the box
        // engine's — reports depths 0..9. The engine reports only a DEPTH and
        // the names live here, so a panel sized to six rows answers `depth == 7`
        // ("armed") by painting six green OKs under a strategy that is blocked:
        // the exact failure this whole section exists to end. Size to the
        // deepest ladder, never to the shortest.
        private const int GateRows = 10;

        // The BOX ladder — the exact strings the box engine passes as the first
        // argument of `Gate.Set`, in its own evaluation order (§6.2). Index i
        // here IS depth i there; if the engine inserts a gate, it inserts one
        // here on the same line or the panel starts naming the wrong blocker.
        private static readonly string[] BoxGates =
        {
            "atr warm", "box warming", "box", "box valid", "auto-trade",
            "window", "budget", "armed", "break", "arms"
        };

        // The CLOUD ladder, DERIVED from the cloud engine's own `Gate.Set`
        // depths (§5) rather than asserted here — that mapping is the cloud
        // engine's to publish, and this array follows it. It is SHORTER than the
        // box's, which is why FillGates renders `names.Length` rows and blanks
        // the rest: a cloud blocker labelled with a box gate name is worse than
        // no label at all. The GateVal[1]/[2] fills in FillGates are indices
        // into THIS array — move them if it moves.
        private static readonly string[] CloudGates = { "atr warm", "regime", "token", "gold candle", "window", "budget" };

        private TextBlock _headline;
        private readonly TextBlock[] _gateMark = new TextBlock[GateRows];
        private readonly TextBlock[] _gateName = new TextBlock[GateRows];
        private readonly TextBlock[] _gateVal = new TextBlock[GateRows];
```

Add the builder to `#region Chrome`:

```csharp
        private UIElement BuildGateSection()
        {
            StackPanel s = new StackPanel();
            s.Children.Add(Section("WHY NO TRADE"));

            // The headline answers the question in words. Three numbers that did
            // not exist in v1 live here: how far the nearest actionable price
            // is, how many bars are left on the armed trigger, and what the
            // token is doing.
            _headline = Label("--");
            _headline.TextWrapping = TextWrapping.Wrap;
            _headline.Margin = new Thickness(0, 0, 0, 4);
            s.Children.Add(_headline);

            for (int i = 0; i < GateRows; i++)
            {
                _gateMark[i] = new TextBlock
                {
                    Text = "\u00B7",
                    Foreground = DimBrush,
                    FontSize = 11,
                    Width = 14,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _gateName[i] = Small("");
                _gateVal[i] = Small("");

                StackPanel left = new StackPanel { Orientation = Orientation.Horizontal };
                left.Children.Add(_gateMark[i]);
                left.Children.Add(_gateName[i]);
                s.Children.Add(Row2(left, _gateVal[i]));
            }

            s.Children.Add(Rule());
            return s;
        }
```

And add it to the body in `BuildPanel`, right after `_body` is created:

```csharp
                _body.Children.Add(BuildGateSection());
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -5`
Expected: PASS — `ALL PASS (n checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxTypes.cs ninjascript/BreakBoxPanel.cs tests/HistoryTests.cs && \
git commit -m "feat(panel): the WHY NO TRADE gate ladder

RowState is pure and asserted: passed / blocker / never evaluated. The
rows below the blocker were short-circuited, so rendering them as either
OK or FAILED is invented. This is the section that replaces a panel
reading READY next to (out of band) for an hour.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 66: The engine log ring buffer

**Files:**
- Modify: `ninjascript/BreakBoxHistory.cs` (add `BbLogRing`)
- Modify: `ninjascript/BreakBoxPanel.cs` (fields, `#region Chrome`, `BuildPanel` body)
- Test: `tests/HistoryTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: **one name not in the shared contract:** `public sealed class BbLogRing` in `BreakBoxHistory.cs` — `Push(string ts, string text)`, `Newest(int i)`, `Count`. It lives in the pure file so newest-first ordering is an assert rather than a thing you check by squinting at a chart. · `private readonly BbLogRing _log` on the strategy · `private void EngineLog(string)`

- [ ] **Step 1: Write the failing test**

Add to `tests/HistoryTests.cs`, plus its call in `Run()`:

```csharp
    public static void Run()
    {
        RoundTrip();
        EquityAndHash();
        WriteGuard();
        GateLadder();
        LogRing();
    }

    private static void LogRing()
    {
        T.Section("Panel — engine log ring");

        BbLogRing r = new BbLogRing(3);
        T.CheckInt(r.Count, 0, "empty");
        T.Check(r.Newest(0) == "", "reading an empty ring yields a blank, not an exception");

        r.Push("12:41", "armed cloud_long @ 29867.50");
        T.CheckInt(r.Count, 1, "one entry");
        T.Check(r.Newest(0) == "12:41  armed cloud_long @ 29867.50", "HH:mm two spaces text");

        r.Push("12:46", "suppressed: box (cloud armed)");
        r.Push("12:52", "token killed - closed through E50");
        // NEWEST FIRST. The ladder says why now; the log says what happened
        // while you were away, and the thing you were away for is the last one.
        T.Check(r.Newest(0).StartsWith("12:52", StringComparison.Ordinal), "newest first");
        T.Check(r.Newest(2).StartsWith("12:41", StringComparison.Ordinal), "oldest last");

        r.Push("12:55", "filled");
        T.CheckInt(r.Count, 3, "the ring does not grow");
        T.Check(r.Newest(0).StartsWith("12:55", StringComparison.Ordinal), "the new entry is newest");
        T.Check(r.Newest(2).StartsWith("12:46", StringComparison.Ordinal), "the oldest fell off");
        T.Check(r.Newest(3) == "", "reading past the end is blank");

        // A repeated block transition would otherwise push the same line three
        // times and evict the two entries that explained it.
        r.Push("12:56", "filled");
        T.Check(r.Newest(1).StartsWith("12:55", StringComparison.Ordinal), "a repeat is not pushed twice");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error CS|FAIL|ALL PASS"`
Expected: FAIL with `error CS0246: The type or namespace name 'BbLogRing' could not be found`

- [ ] **Step 3: Write minimal implementation**

Append to `ninjascript/BreakBoxHistory.cs`, inside `namespace BreakBoxCore`:

```csharp
    // The engine log (§9.4). Lives in the pure file for one reason: newest-first
    // ordering across a wrap is the kind of off-by-one you cannot see on a chart
    // — three plausible lines in the wrong order look exactly like three lines
    // in the right order.
    public sealed class BbLogRing
    {
        private readonly string[] _buf;
        private int _next;
        private int _count;
        private string _lastText = "";

        public BbLogRing(int size)
        {
            _buf = new string[size < 1 ? 1 : size];
        }

        public int Count { get { return _count; } }

        public void Push(string ts, string text)
        {
            if (text == null)
                text = "";
            // Every transition into a new Block pushes. Without this, a block
            // that persists for forty bars evicts the two entries that
            // explained how it got there.
            if (text == _lastText)
                return;
            _lastText = text;
            _buf[_next] = (ts == null ? "" : ts) + "  " + text;
            _next = (_next + 1) % _buf.Length;
            if (_count < _buf.Length)
                _count++;
        }

        // 0 = the most recent entry.
        public string Newest(int i)
        {
            if (i < 0 || i >= _count)
                return "";
            int idx = _next - 1 - i;
            while (idx < 0)
                idx += _buf.Length;
            return _buf[idx] == null ? "" : _buf[idx];
        }
    }
```

In `ninjascript/BreakBoxPanel.cs`, add to the fields region:

```csharp
        private static readonly int LogRows = 3;
        private readonly BbLogRing _log = new BbLogRing(3);
        private readonly TextBlock[] _logText = new TextBlock[3];
```

Add to `#region Chrome`:

```csharp
        private UIElement BuildLogSection()
        {
            StackPanel s = new StackPanel();
            s.Children.Add(Section("ENGINE LOG"));
            for (int i = 0; i < LogRows; i++)
            {
                _logText[i] = Small("");
                _logText[i].TextTrimming = TextTrimming.CharacterEllipsis;
                s.Children.Add(_logText[i]);
            }
            s.Children.Add(Rule());
            return s;
        }
```

Add to `#region Panel actions (strategy thread)` — the single push point, called from the strategy thread only:

```csharp
        // Called from OnBarUpdate and the order handlers, never from WPF. The
        // ring is plain fields with no lock because there is exactly one writer
        // thread and the reader only ever runs inside the batched dispatcher
        // callback, which reads a COPY taken on this thread.
        private void EngineLog(string text)
        {
            _log.Push(Time[0].ToString("HH:mm", CultureInfo.InvariantCulture), text);
        }
```

And add the section to `BuildPanel`'s body, after the gate section:

```csharp
                _body.Children.Add(BuildLogSection());
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -5`
Expected: PASS — `ALL PASS (n checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxHistory.cs ninjascript/BreakBoxPanel.cs tests/HistoryTests.cs && \
git commit -m "feat(panel): engine log ring, newest first and de-duplicated

Pure and asserted: three plausible lines in the wrong order look exactly
like three lines in the right order. Repeats are dropped so a block that
persists for forty bars does not evict the entries that explained it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 67: CONTROLS and SESSION — toggles off the WPF thread (B5)

**Files:**
- Modify: `ninjascript/BreakBoxPanel.cs` (fields, `#region Chrome`, `BuildPanel` body, `#region Panel actions`)
- Test: `scripts/check.sh`

**Interfaces:**
- Consumes: from Phase 3's shell — `private bool _uiCloudOn;` and `private bool _uiBreakOn, _uiLongOn, _uiShortOn;` (the box engine's toggle is `_uiBreakOn`, named after `EnableBreak`; only the button caption says "Box"), `private double _uiRiskMult;`, `private BbStopSource _uiStopSource;`, `private void BuildConfigs();`, `private int BarSeconds();`
- Produces: `private void Rebuild()` (the B5 bridge) · `private TextBlock _sessionA, _sessionB` · `private Button _cloudBtn, _boxBtn, _buyBtn, _sellBtn` · `private readonly Button[] _riskBtns, _slBtns`

- [ ] **Step 1: Write the failing test**

Add the two sections to `BuildPanel`'s body list, after the log section:

```csharp
                _body.Children.Add(BuildControlsSection());
                _body.Children.Add(BuildSessionSection());
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | grep -E "error CS|compiles clean"`
Expected: FAIL with `error CS0103: The name 'BuildControlsSection' does not exist in the current context`

- [ ] **Step 3: Write minimal implementation**

Add to the fields region of `ninjascript/BreakBoxPanel.cs`:

```csharp
        private Button _cloudBtn, _boxBtn, _buyBtn, _sellBtn;
        private readonly Button[] _riskBtns = new Button[3];
        private readonly Button[] _slBtns = new Button[5];
        private static readonly double[] RiskLevels = { 0.5, 1.0, 1.5 };
        // Five, because BbStopSource has five (§9.5). `MA` changes MEANING with
        // MaPeriod — at RibbonSlow it is "the far ribbon edge" — which is why
        // the SESSION block below names the active one instead of leaving the
        // lit button to imply it.
        private static readonly string[] SlNames = { "Cndl", "Swng", "MA", "E50", "Man" };
        private TextBlock _sessionA, _sessionB;
```

Add to `#region Chrome`:

```csharp
        private UIElement BuildControlsSection()
        {
            StackPanel s = new StackPanel();
            s.Children.Add(Section("CONTROLS"));

            _cloudBtn = Toggle("Cloud", _uiCloudOn, delegate
            {
                _uiCloudOn = !_uiCloudOn;
                Paint(_cloudBtn, _uiCloudOn);
                Rebuild();
            });
            // Caption "Box", field `_uiBreakOn`. The engine is the break engine
            // and Phase 3 names the field after its `EnableBreak` property; the
            // box is what the user sees it draw, so that is what the button says.
            _boxBtn = Toggle("Box", _uiBreakOn, delegate
            {
                _uiBreakOn = !_uiBreakOn;
                Paint(_boxBtn, _uiBreakOn);
                Rebuild();
            });
            s.Children.Add(Row2(Small("Engine"), Cols(_cloudBtn, _boxBtn)));

            _buyBtn = Toggle("Buy", _uiLongOn, delegate
            {
                _uiLongOn = !_uiLongOn;
                Paint(_buyBtn, _uiLongOn);
                Rebuild();
            });
            _sellBtn = Toggle("Sell", _uiShortOn, delegate
            {
                _uiShortOn = !_uiShortOn;
                Paint(_sellBtn, _uiShortOn);
                Rebuild();
            });
            s.Children.Add(Row2(Small("Side"), Cols(_buyBtn, _sellBtn)));

            UIElement[] risk = new UIElement[RiskLevels.Length];
            for (int i = 0; i < RiskLevels.Length; i++)
            {
                int idx = i;
                _riskBtns[i] = Toggle(RiskLevels[i].ToString("0.#", CultureInfo.InvariantCulture) + "x",
                    Math.Abs(_uiRiskMult - RiskLevels[i]) < 1e-9, delegate
                    {
                        _uiRiskMult = RiskLevels[idx];
                        for (int k = 0; k < _riskBtns.Length; k++)
                            Paint(_riskBtns[k], k == idx);
                        // NO Rebuild(): risk scales size, not the decision, and
                        // it is excluded from the config hash for the same
                        // reason (§10). Rebuilding here would be harmless and
                        // misleading.
                    });
                risk[i] = _riskBtns[i];
            }
            s.Children.Add(Row2(Small("Risk"), Cols(risk)));

            UIElement[] sl = new UIElement[SlNames.Length];
            for (int i = 0; i < SlNames.Length; i++)
            {
                int idx = i;
                _slBtns[i] = Toggle(SlNames[i], (int)_uiStopSource == i, delegate
                {
                    _uiStopSource = (BbStopSource)idx;
                    for (int k = 0; k < _slBtns.Length; k++)
                        Paint(_slBtns[k], k == idx);
                    Rebuild();
                });
                sl[i] = _slBtns[i];
            }
            s.Children.Add(Row2(Small("Stop"), Cols(sl)));

            s.Children.Add(Rule());
            return s;
        }

        private UIElement BuildSessionSection()
        {
            StackPanel s = new StackPanel();
            s.Children.Add(Section("SESSION"));
            _sessionA = Small("--");
            _sessionB = Small("--");
            s.Children.Add(_sessionA);
            s.Children.Add(_sessionB);
            s.Children.Add(Rule());
            return s;
        }
```

Add to `#region Panel actions (strategy thread)`:

```csharp
        // B5. Every toggle used to call BuildConfigs() straight out of its click
        // handler — i.e. on the WPF thread — swapping _cfg and the engine out
        // from under a running OnBarUpdate. Flipping the bool is a single
        // aligned write and survives that; rebuilding the config object does
        // not. Both now happen on NinjaScript's thread, in order.
        private void Rebuild()
        {
            Dispatch(o => BuildConfigs());
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -5`
Expected: PASS — `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxPanel.cs && \
git commit -m "fix(panel): route config rebuilds through TriggerCustomEvent (B5)

Toggles called BuildConfigs() on the WPF thread, swapping the engine out
from under OnBarUpdate. Risk deliberately does NOT rebuild: it scales
size, not the decision, which is the same reason it is out of the hash.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 68: The history chart — Polyline, Polygon, dashed zero, three views

**Files:**
- Modify: `ninjascript/BreakBoxHistory.cs` (append `View` and `SparkPoints` to `BbHistory`)
- Modify: `ninjascript/BreakBoxPanel.cs` (usings, fields, `#region Chrome`, `BuildPanel` body)
- Test: `tests/HistoryTests.cs`

**Interfaces:**
- Consumes: `BbHistory.CumulativeEquity` (Task 61) · `_history` (Task 63)
- Produces: `public static List<BbTradeRecord> BbHistory.View(IReadOnlyList<BbTradeRecord>, string view)` · `public static double[] BbHistory.SparkPoints(double[] cum, double w, double h, out double zeroY)` · `private string _histView` · `private WPolyline _equityLine` · `private WPolygon _equityFill` · `private WLine _zeroLine`

- [ ] **Step 1: Write the failing test**

Add to `tests/HistoryTests.cs`, plus its call in `Run()`:

```csharp
    public static void Run()
    {
        RoundTrip();
        EquityAndHash();
        WriteGuard();
        GateLadder();
        LogRing();
        ChartMath();
    }

    private static void ChartMath()
    {
        T.Section("Panel — history chart math");

        List<BbTradeRecord> rows = new List<BbTradeRecord>();
        for (int i = 0; i < 120; i++)
        {
            BbTradeRecord r = Rec();
            r.Ts = new DateTime(2026, 8, 1).AddDays(i / 6).AddHours(9 + i % 6);
            r.Pnl = 10.0;
            rows.Add(r);
        }

        T.CheckInt(BbHistory.View(rows, "100t").Count, 100, "100t takes the last hundred trades");
        // Windowed off the NEWEST RECORD, never DateTime.Now: a Replay file's
        // last trade is months old, and "today" against the wall clock would
        // render an empty chart with no explanation anywhere on the panel.
        T.CheckInt(BbHistory.View(rows, "today").Count, 6, "today = the newest record's own day");
        T.Check(BbHistory.View(rows, "20d").Count > 6, "20d is wider than today");
        T.CheckInt(BbHistory.View(new List<BbTradeRecord>(), "20d").Count, 0, "an empty file yields no view");
        T.CheckInt(BbHistory.View(null, "20d").Count, 0, "a null list does not throw");

        double zeroY;
        T.CheckInt(BbHistory.SparkPoints(new double[0], 100, 50, out zeroY).Length, 0, "no points from no trades");

        // A flat curve is the divide-by-zero: span 0. It draws down the middle.
        double[] flat = BbHistory.SparkPoints(new double[] { 0.0, 0.0, 0.0 }, 100, 50, out zeroY);
        T.CheckClose(zeroY, 25.0, "a flat curve puts the zero line mid-box");
        T.CheckClose(flat[1], 25.0, "and the curve on it");
        T.CheckClose(flat[4], 100.0, "the last point is at the right edge");

        // An all-negative curve: zero is still IN frame, pinned to the top.
        // Off-canvas would leave the reader with no reference at all.
        double[] down = BbHistory.SparkPoints(new double[] { -10.0, -20.0 }, 100, 50, out zeroY);
        T.CheckClose(zeroY, 0.0, "an underwater curve keeps zero at the top edge");
        T.CheckClose(down[3], 50.0, "the worst point sits on the floor");

        // y is inverted: WPF's origin is top-left, so a PROFIT must have a
        // SMALLER y. Getting this wrong renders every winning run as a slide.
        double[] up = BbHistory.SparkPoints(new double[] { 0.0, 100.0 }, 100, 50, out zeroY);
        T.Check(up[3] < up[1], "profit goes UP the screen");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error CS|FAIL|ALL PASS"`
Expected: FAIL with `error CS0117: 'BbHistory' does not contain a definition for 'View'`

- [ ] **Step 3: Write minimal implementation**

Append inside `BbHistory` in `ninjascript/BreakBoxHistory.cs`:

```csharp
        // The panel's three views. `today` and `20d` are windows measured from
        // the NEWEST RECORD, not from DateTime.Now: a Replay file's last trade
        // is months old, and a wall-clock "today" would render an empty chart
        // with nothing on the panel to explain it.
        public static List<BbTradeRecord> View(IReadOnlyList<BbTradeRecord> rows, string view)
        {
            List<BbTradeRecord> outp = new List<BbTradeRecord>();
            if (rows == null || rows.Count == 0)
                return outp;

            if (view == "100t")
            {
                int from = rows.Count > 100 ? rows.Count - 100 : 0;
                for (int i = from; i < rows.Count; i++)
                    outp.Add(rows[i]);
                return outp;
            }

            int days = view == "today" ? 1 : 20;
            DateTime cut = rows[rows.Count - 1].Ts.Date.AddDays(1 - days);
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Ts.Date >= cut)
                    outp.Add(rows[i]);
            return outp;
        }

        // Maps a cumulative-equity curve onto panel pixels: x,y pairs plus the y
        // of the zero baseline. y is INVERTED because WPF's origin is top-left —
        // a profit has to have a smaller y or every winning run renders as a
        // slide.
        //
        // Zero is always in frame: it is the reference the whole chart is read
        // against, so an all-negative curve pins it to the top edge rather than
        // scrolling it off the canvas.
        public static double[] SparkPoints(double[] cum, double w, double h, out double zeroY)
        {
            zeroY = h * 0.5;
            if (cum == null || cum.Length == 0 || w <= 0.0 || h <= 0.0)
                return new double[0];

            double lo = 0.0, hi = 0.0;
            for (int i = 0; i < cum.Length; i++)
            {
                if (cum[i] < lo) lo = cum[i];
                if (cum[i] > hi) hi = cum[i];
            }

            double dx = cum.Length == 1 ? 0.0 : w / (cum.Length - 1);
            double[] pts = new double[cum.Length * 2];
            double span = hi - lo;

            // The degenerate case that makes this function worth testing: a
            // curve that never moves divides by zero. It draws down the middle.
            if (span <= 0.0)
            {
                for (int i = 0; i < cum.Length; i++)
                {
                    pts[i * 2] = i * dx;
                    pts[i * 2 + 1] = zeroY;
                }
                return pts;
            }

            zeroY = h * hi / span;
            for (int i = 0; i < cum.Length; i++)
            {
                pts[i * 2] = i * dx;
                pts[i * 2 + 1] = h * (hi - cum[i]) / span;
            }
            return pts;
        }
```

In `ninjascript/BreakBoxPanel.cs`, add to the using block (`:22-32`):

```csharp
using System.Windows.Shapes;
// Aliased, and this is not style. check.sh hoists every file's usings into ONE
// compilation unit, which drags BreakBoxStrategy.cs's
// `using NinjaTrader.NinjaScript.DrawingTools` into scope here — and that
// namespace declares its own Line, Polygon and Polyline. Unaliased, the
// combined build (and only the combined build) fails CS0104 ambiguous
// reference, which is a spectacularly confusing way to lose an afternoon.
using WLine = System.Windows.Shapes.Line;
using WPolyline = System.Windows.Shapes.Polyline;
using WPolygon = System.Windows.Shapes.Polygon;
```

Add to the fields region:

```csharp
        // Chart geometry, in DIP. 300 wide minus 2x10 body margin minus 16 of
        // slack for the scrollbar.
        private const double ChartW = 254;
        private const double ChartH = 64;

        private string _histView = "20d";
        private readonly Button[] _viewBtns = new Button[3];
        private static readonly string[] ViewNames = { "today", "20d", "100t" };
        private TextBlock _equityText, _statsText;
        private Canvas _chart;
        private WPolyline _equityLine;
        private WPolygon _equityFill;
        private WLine _zeroLine;
        private ColumnDefinition _wCol, _beCol, _lCol;
        private readonly TextBlock[] _tradeText = new TextBlock[3];
        private readonly Border[] _tradeBar = new Border[3];
```

Add to `#region Chrome`:

```csharp
        private UIElement BuildHistorySection()
        {
            StackPanel s = new StackPanel();

            UIElement[] views = new UIElement[ViewNames.Length];
            for (int i = 0; i < ViewNames.Length; i++)
            {
                int idx = i;
                _viewBtns[i] = Toggle(ViewNames[i], ViewNames[i] == _histView, delegate
                {
                    _histView = ViewNames[idx];
                    for (int k = 0; k < _viewBtns.Length; k++)
                        Paint(_viewBtns[k], k == idx);
                    // No Rebuild(): the view is a lens on data already in
                    // memory. It must not touch the trading config.
                });
                views[i] = _viewBtns[i];
            }
            s.Children.Add(Row2(Section("HISTORY"), Cols(views)));

            // The dominant number. 22px because it is the one thing on this
            // panel a human reads from across the room.
            _equityText = new TextBlock
            {
                Text = "--",
                Foreground = TextBrush,
                FontSize = 22,
                Margin = new Thickness(0, 2, 0, 2)
            };
            s.Children.Add(_equityText);

            _chart = new Canvas { Height = ChartH, Width = ChartW, Margin = new Thickness(0, 2, 0, 6) };
            _zeroLine = new WLine
            {
                X1 = 0,
                X2 = ChartW,
                Stroke = DimBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection(new double[] { 2, 3 })
            };
            _equityFill = new WPolygon { Fill = new SolidColorBrush(Color.FromArgb(0x28, 0x00, 0xC8, 0xFF)) };
            _equityLine = new WPolyline { Stroke = OnBrush, StrokeThickness = 1.5 };
            // Baseline under the fill under the line: the line is the data and
            // must never be the thing that gets covered.
            _chart.Children.Add(_zeroLine);
            _chart.Children.Add(_equityFill);
            _chart.Children.Add(_equityLine);
            s.Children.Add(_chart);

            _statsText = Small("--");
            s.Children.Add(_statsText);

            // Stacked W / BE / L. The widths are star weights set at update
            // time, so WPF does the arithmetic and a zero-count segment simply
            // collapses instead of rendering a 1px sliver.
            Grid bar = new Grid { Height = 6, Margin = new Thickness(0, 3, 0, 6) };
            _wCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            _beCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            _lCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            bar.ColumnDefinitions.Add(_wCol);
            bar.ColumnDefinitions.Add(_beCol);
            bar.ColumnDefinitions.Add(_lCol);
            Border wSeg = new Border { Background = OkBrush };
            Border beSeg = new Border { Background = DimBrush };
            Border lSeg = new Border { Background = LossBrush };
            Grid.SetColumn(wSeg, 0); Grid.SetColumn(beSeg, 1); Grid.SetColumn(lSeg, 2);
            bar.Children.Add(wSeg); bar.Children.Add(beSeg); bar.Children.Add(lSeg);
            s.Children.Add(bar);

            // The last three trades. The row BACKGROUND is the magnitude bar —
            // a separate bar column would cost 60 of the 300 DIP and say the
            // same thing.
            for (int i = 0; i < 3; i++)
            {
                Grid g = new Grid { Height = 16, Margin = new Thickness(0, 1, 0, 1) };
                _tradeBar[i] = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xC3, 0x8C)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 0
                };
                _tradeText[i] = Small("");
                g.Children.Add(_tradeBar[i]);
                g.Children.Add(_tradeText[i]);
                s.Children.Add(g);
            }

            return s;
        }
```

And add it to `BuildPanel`'s body, last:

```csharp
                _body.Children.Add(BuildHistorySection());
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -5`
Expected: PASS — `ALL PASS (n checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxHistory.cs ninjascript/BreakBoxPanel.cs tests/HistoryTests.cs && \
git commit -m "feat(panel): history chart — Polyline + Polygon on a dashed zero

Views window off the newest RECORD, not the wall clock, so a Replay file
still renders. SparkPoints is pure and asserted on the two cases that
bite: a flat curve (divide by zero) and an all-negative one (zero pinned
to the top rather than off canvas). WPF shapes aliased — the combined
compilation unit drags DrawingTools' Line/Polygon/Polyline into scope.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 69: One batched dispatcher update per bar

**Files:**
- Modify: `ninjascript/BreakBoxPanel.cs:369-432` (the `Readouts` region — `UpdatePanelStatus` and `UpdateHud` in full)
- Modify: `ninjascript/BreakBoxStrategy.cs:378-397` (the tail of `OnBarUpdate`, which calls `UpdateHud()` twice)
- Test: `scripts/check.sh`

**Interfaces:**
- Consumes: `BbGateReport.RowState` (Task 65) · `BbLogRing.Newest` (Task 66) · `BbHistory.View` / `CumulativeEquity` / `SparkPoints` (Tasks 61, 68) · `_history`, `_cfgHash` (Task 63) · from Phases 2–3: `private BbCloudState _cloudState;`, `private BbEngineState _engState;`, `private BbEntryEngine _owningEngine;`, `private int BarSeconds();`, `_uiCloudOn`
- Produces: `private sealed class PanelSnap` · `private void UpdatePanelStatus()` (now the only panel entry point per bar)

- [ ] **Step 1: Write the failing test**

Replace the whole `#region Readouts` (`:369-432`) of `ninjascript/BreakBoxPanel.cs` with the caller half:

```csharp
        #region Readouts

        // ONE snapshot per bar, built on the STRATEGY thread and applied by
        // exactly ONE dispatcher callback. v1 posted five separate InvokeAsync
        // closures per bar, each capturing live fields — which is both a torn
        // read (the fields move between callbacks) and five context switches on
        // a thread the strategy is forbidden from blocking.
        private sealed class PanelSnap
        {
            public string Status = "", Headline = "", SessionA = "", SessionB = "", Equity = "", Stats = "";
            public int StatusState;                             // 0 dim, 1 ok, 2 warn, 3 loss
            public readonly string[] GateName = new string[GateRows];
            public readonly string[] GateVal = new string[GateRows];
            // 0 passed · 1 the blocker · 2 never evaluated · 3 not a gate on the
            // active engine's ladder at all (the cloud's is shorter than the box's)
            public readonly int[] GateState = new int[GateRows];
            public readonly string[] Log = new string[3];
            public double[] Pts = new double[0];
            public double ZeroY;
            public double WinN, BeN, LossN;
            public readonly string[] TradeText = new string[3];
            public readonly double[] TradeBar = new double[3];
            public readonly int[] TradeState = new int[3];      // 0 dim (other config), 1 win, 2 loss
        }

        private void UpdatePanelStatus()
        {
            if (_panelRoot == null || ChartControl == null)
                return;

            PanelSnap s = new PanelSnap();
            FillStatus(s);
            FillGates(s);
            FillHistory(s);
            for (int i = 0; i < 3; i++)
                s.Log[i] = _log.Newest(i);

            ChartControl.Dispatcher.InvokeAsync(new Action(() => ApplySnap(s)));
        }

        #endregion
```

And drop the now-dead HUD calls from `OnBarUpdate` in `ninjascript/BreakBoxStrategy.cs` — the `UpdateHud();` before the `return` in the flatten branch and the pair at the tail become:

```csharp
            if (TimeToFlatten(secs))
            {
                FlattenAll("session_window");
                UpdatePanelStatus();
                return;
            }
```

```csharp
            UpdatePanelStatus();
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | grep -E "error CS|compiles clean"`
Expected: FAIL with `error CS0103: The name 'FillStatus' does not exist in the current context` (and `FillGates`, `FillHistory`, `ApplySnap`, plus `UpdateHud` no longer defined)

- [ ] **Step 3: Write minimal implementation**

Add to `#region Readouts` in `ninjascript/BreakBoxPanel.cs`:

```csharp
        // Shell-level blocks PREEMPT the gate ladder and replace the headline
        // (§9.3). This ordering is the fix for the defect that started the
        // rewrite: v1 showed READY while the box sat "(out of band)" and never
        // connected the two, and the user watched a dead strategy for an hour.
        private void FillStatus(PanelSnap s)
        {
            if (_lockout)
            {
                s.Status = "LOCKED OUT (" + _lockoutWhy + ")";
                s.StatusState = 3;
                s.Headline = _lockoutWhy == "manual"
                    ? "locked by hand — click LOCK OUT again to resume"
                    : "day P&L " + _dayPnl.ToString("C2", CultureInfo.CurrentCulture);
            }
            else if (_inTrade)
            {
                s.Status = "IN TRADE " + (_dir > 0 ? "LONG" : "SHORT");
                s.StatusState = 1;
                s.Headline = _qty + " @ " + _bracket.EntryPx.ToString("0.00", CultureInfo.InvariantCulture)
                           + "  stop " + _bracket.StopPx.ToString("0.00", CultureInfo.InvariantCulture)
                           + (_bracket.BeApplied ? " (BE)" : "");
            }
            else if (_entryPending)
            {
                s.Status = "ENTRY WORKING";
                s.StatusState = 2;
                s.Headline = "trigger " + _pendingAction.TriggerPx.ToString("0.00", CultureInfo.InvariantCulture)
                           + " — " + _entryBarsWaiting + " bars waiting";
            }
            else if (!_atr.IsWarm)
            {
                s.Status = "WARMING";
                s.StatusState = 0;
                s.Headline = "ATR " + _atr.BarsFed + "/" + AtrPeriod + " bars";
            }
            else if (!_uiAutoTrade)
            {
                s.Status = "AUTO-TRADE OFF";
                s.StatusState = 2;
                s.Headline = "the engines still track state — only entries are suppressed";
            }
            else
            {
                s.Status = "READY";
                s.StatusState = 1;
            }

            int secs = Time[0].Hour * 3600 + Time[0].Minute * 60 + Time[0].Second;
            int left = BbMath.HhmmToSecs(FlattenHhmm) - secs;
            if (left < 0) left += 24 * 3600;
            s.SessionA = string.Format(CultureInfo.InvariantCulture,
                "atr {0:0.00}   bar {1}s   flat in {2}h{3:00}m",
                _atr.IsWarm ? _atr.Value : 0.0, BarSeconds(), left / 3600, (left % 3600) / 60);
            // The active stop source is NAMED, not merely lit on a button:
            // `MA` means "far ribbon edge" at MaPeriod = RibbonSlow and
            // something else entirely otherwise, and that is invisible in a
            // three-letter toggle (§9.5).
            s.SessionB = "stop " + _uiStopSource + " (period " + MaPeriod + ")   cfg " + _cfgHash;
        }

        // §4.2: the ladder shown is the report of the engine that would act
        // NEXT under §4.1 ordering — Cloud when it is on, else Box. The two
        // reports are never merged; merging them is how one engine's blocker
        // ends up labelled with the other's gate names.
        private void FillGates(PanelSnap s)
        {
            bool cloud = _uiCloudOn;
            BbGateReport g = cloud ? _cloudState.Gate : _engState.Gate;
            string[] names = cloud ? CloudGates : BoxGates;
            int depth = g == null ? -1 : g.GateDepth;

            for (int i = 0; i < GateRows; i++)
            {
                // The two ladders are not the same length — the box's is ten
                // deep, the cloud's six. Rows past the end of the ACTIVE
                // engine's ladder are marked unused (3) and render as nothing,
                // rather than borrowing the other engine's name for that index.
                // Reaching for `names[i]` unguarded is an IndexOutOfRange on
                // every cloud bar, and padding CloudGates with box names would
                // be the quieter, worse version of the same bug.
                bool has = i < names.Length;
                s.GateName[i] = has ? names[i] : "";
                s.GateState[i] = has ? BbGateReport.RowState(i, depth) : 3;
                s.GateVal[i] = "";
            }

            // Passed rows carry the value the shell can read without reaching
            // into engine internals; the blocker carries the engine's own
            // BlockDetail, which is the only place "has 0.42, needs 0.60" is
            // known. A dimmed row deliberately carries nothing.
            if (s.GateState[0] == 0)
                s.GateVal[0] = _atr.Value.ToString("0.00", CultureInfo.InvariantCulture)
                             + " (" + AtrPeriod + " bars)";

            if (cloud && _cloudState != null)
            {
                if (s.GateState[1] == 0)
                    s.GateVal[1] = (_cloudState.RegimeLatched > 0 ? "long" : _cloudState.RegimeLatched < 0 ? "short" : "none")
                                 + " (latched " + Mins(_cloudState.RegimeLatchedAgeBars) + ")";
                if (s.GateState[2] == 0)
                    s.GateVal[2] = _cloudState.Armed
                        ? "armed " + _cloudState.AgeBars + " bars ago"
                        : "no token — " + _cloudState.BarsSinceLastArm + " bars since";
            }

            // Bounded by the ACTIVE ladder, not by GateRows: a depth the ladder
            // has no name for is an engine/panel mismatch, and writing its
            // detail into a blank row would hide the mismatch instead of it
            // showing up as an unlabelled blocker.
            if (depth >= 0 && depth < names.Length && g != null)
                s.GateVal[depth] = g.BlockDetail == null ? "" : g.BlockDetail;

            if (s.Headline.Length == 0)
                s.Headline = g == null || g.Block == null || g.Block.Length == 0
                    ? "all gates clear — waiting for the trigger bar"
                    : g.Block;
        }

        private string Mins(int bars)
        {
            int sec = bars * BarSeconds();
            return sec < 60 ? sec + "s" : (sec / 60) + "m";
        }

        private void FillHistory(PanelSnap s)
        {
            List<BbTradeRecord> view = BbHistory.View(_history, _histView);
            double[] cum = BbHistory.CumulativeEquity(view);
            double zeroY;
            // The POINTS are computed here, as plain doubles. PointCollection is
            // a Freezable and building one on this thread is exactly the
            // cross-thread ownership bug the frozen brushes avoid.
            s.Pts = BbHistory.SparkPoints(cum, ChartW, ChartH, out zeroY);
            s.ZeroY = zeroY;

            double total = cum.Length == 0 ? 0.0 : cum[cum.Length - 1];
            s.Equity = (total >= 0 ? "+" : "") + total.ToString("C2", CultureInfo.CurrentCulture);

            double biggest = 1.0;
            for (int i = 0; i < view.Count; i++)
            {
                if (view[i].Pnl > 0) s.WinN++;
                else if (view[i].Pnl < 0) s.LossN++;
                else s.BeN++;
                double abs = Math.Abs(view[i].Pnl);
                if (abs > biggest) biggest = abs;
            }
            double decided = s.WinN + s.LossN;
            s.Stats = string.Format(CultureInfo.InvariantCulture,
                "{0} trades   W{1} BE{2} L{3}   ·   {4} win",
                view.Count, (int)s.WinN, (int)s.BeN, (int)s.LossN,
                decided > 0 ? ((100.0 * s.WinN / decided).ToString("0", CultureInfo.InvariantCulture) + "%") : "--");

            for (int i = 0; i < 3; i++)
            {
                int idx = view.Count - 1 - i;
                if (idx < 0)
                {
                    s.TradeText[i] = "";
                    s.TradeBar[i] = 0.0;
                    continue;
                }
                BbTradeRecord r = view[idx];
                s.TradeText[i] = string.Format(CultureInfo.InvariantCulture, "#{0} {1} {2}   {3}",
                    idx + 1, r.Dir > 0 ? "LONG " : "SHORT",
                    r.Ts.ToString("HH:mm", CultureInfo.InvariantCulture),
                    (r.Pnl >= 0 ? "+" : "") + r.Pnl.ToString("0.00", CultureInfo.InvariantCulture));
                s.TradeBar[i] = ChartW * Math.Abs(r.Pnl) / biggest;
                // Trades from another configuration render DIMMED (§10). A
                // parameter change has to show as a visible seam — silently
                // mixing them is the contamination the hash exists to expose.
                s.TradeState[i] = r.CfgHash != _cfgHash ? 0 : (r.Pnl >= 0 ? 1 : 2);
            }
        }

        // The ONLY code in this file that runs on the WPF thread. It reads the
        // snapshot and nothing else — no strategy field is touched from here,
        // which is what makes the whole arrangement safe.
        private void ApplySnap(PanelSnap s)
        {
            if (_panelRoot == null)
                return;

            Brush st = s.StatusState == 1 ? OkBrush : s.StatusState == 2 ? WarnBrush
                     : s.StatusState == 3 ? LossBrush : DimBrush;
            if (_statusDot != null) _statusDot.Foreground = st;
            if (_statusText != null) _statusText.Text = s.Status;
            if (_headline != null) _headline.Text = s.Headline;
            if (_sessionA != null) _sessionA.Text = s.SessionA;
            if (_sessionB != null) _sessionB.Text = s.SessionB;

            for (int i = 0; i < GateRows; i++)
            {
                if (_gateName[i] == null) continue;
                _gateName[i].Text = s.GateName[i];
                _gateVal[i].Text = s.GateVal[i];
                if (s.GateState[i] == 3)
                {
                    // Past the end of the active engine's ladder: not a gate at
                    // all. Blank, not "not evaluated" — the cloud engine does
                    // not HAVE four more gates it skipped.
                    _gateMark[i].Text = ""; _gateVal[i].Text = "";
                }
                else if (s.GateState[i] == 0)
                {
                    _gateMark[i].Text = "OK"; _gateMark[i].Foreground = OkBrush;
                    _gateName[i].Foreground = TextBrush; _gateVal[i].Foreground = DimBrush;
                }
                else if (s.GateState[i] == 1)
                {
                    _gateMark[i].Text = "\u2715"; _gateMark[i].Foreground = WarnBrush;
                    _gateName[i].Foreground = WarnBrush; _gateVal[i].Foreground = WarnBrush;
                }
                else
                {
                    _gateMark[i].Text = "\u00B7"; _gateMark[i].Foreground = DimBrush;
                    _gateName[i].Foreground = DimBrush;
                    _gateVal[i].Text = "not evaluated"; _gateVal[i].Foreground = DimBrush;
                }
            }

            for (int i = 0; i < LogRows; i++)
                if (_logText[i] != null) _logText[i].Text = s.Log[i];

            if (_equityText != null)
            {
                _equityText.Text = s.Equity;
                _equityText.Foreground = s.Equity.StartsWith("-", StringComparison.Ordinal) ? LossBrush : OkBrush;
            }
            if (_statsText != null) _statsText.Text = s.Stats;

            if (_equityLine != null)
            {
                PointCollection line = new PointCollection(s.Pts.Length / 2);
                for (int i = 0; i < s.Pts.Length; i += 2)
                    line.Add(new Point(s.Pts[i], s.Pts[i + 1]));
                _equityLine.Points = line;

                // The fill is the same polyline closed down to the zero
                // baseline, not to the bottom of the box: an underwater segment
                // has to shade the WRONG side of zero or the picture lies.
                PointCollection fill = new PointCollection(line.Count + 2);
                if (line.Count > 0)
                {
                    fill.Add(new Point(line[0].X, s.ZeroY));
                    for (int i = 0; i < line.Count; i++) fill.Add(line[i]);
                    fill.Add(new Point(line[line.Count - 1].X, s.ZeroY));
                }
                _equityFill.Points = fill;
                _zeroLine.Y1 = s.ZeroY;
                _zeroLine.Y2 = s.ZeroY;
            }

            // Star weights, so a zero-count segment collapses instead of
            // rendering a misleading sliver.
            if (_wCol != null)
            {
                _wCol.Width = new GridLength(s.WinN, GridUnitType.Star);
                _beCol.Width = new GridLength(s.BeN, GridUnitType.Star);
                _lCol.Width = new GridLength(s.LossN, GridUnitType.Star);
            }

            for (int i = 0; i < 3; i++)
            {
                if (_tradeText[i] == null) continue;
                _tradeText[i].Text = s.TradeText[i];
                _tradeText[i].Foreground = s.TradeState[i] == 0 ? DimBrush : TextBrush;
                _tradeBar[i].Width = s.TradeBar[i];
                _tradeBar[i].Background = s.TradeState[i] == 0
                    ? new SolidColorBrush(Color.FromArgb(0x18, 0x6A, 0x72, 0x7E))
                    : s.TradeState[i] == 1
                        ? new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xC3, 0x8C))
                        : new SolidColorBrush(Color.FromArgb(0x30, 0xD9, 0x53, 0x4F));
            }

            Paint(_lockBtn, _lockout);
            Paint(_autoBtn, _uiAutoTrade);
        }
```

Add the two usings `BreakBoxPanel.cs` now needs (`System.Collections.Generic` for `List<BbTradeRecord>`, `System.Windows` already present for `Point`):

```csharp
using System.Collections.Generic;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -6`
Expected: PASS — `ALL PASS (n checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxPanel.cs ninjascript/BreakBoxStrategy.cs && \
git commit -m "feat(panel): one batched snapshot per bar drives the whole panel

Built on the strategy thread, applied by a single dispatcher callback
that reads nothing but the snapshot. v1 posted five closures per bar,
each capturing live fields — a torn read and five context switches on a
thread the strategy must not block.

Shell blocks now preempt the ladder, so READY can no longer sit above
(out of band), and trades from another cfgHash render dimmed.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```
## Phase 5 — BreakBoxVision indicator

### Task 81: `BreakBoxVision.cs` skeleton — plots, its OWN indicators, its OWN dials through the SHARED `BbScale`

**Files:**
- Create: `projects/Trading/BreakBox/ninjascript/BreakBoxVision.cs`
- Modify: `projects/Trading/BreakBox/scripts/check.sh:28` (the `FILES=(...)` array)
- Test: `projects/Trading/BreakBox/scripts/check.sh` (the nt8c compile gate — Vision is an NT8 file and has no runner test)

**Interfaces:**
- Consumes: `BreakBoxCore.WilderAtr`, `BreakBoxCore.Ema` (`BreakBoxTypes.cs:41,82`), `BbScale.Bars` / `BbScale.EstimateBarSeconds` / `BbScale.FallbackSeconds` / `BbScale.MinEstimateSamples` (Phase 1, Tasks 1-2 — Vision converts through the same scaler as the strategy and hand-rolls nothing), `BbCloudConfig` field names from spec §5.4 after conversion (`RibbonFast`, `RibbonSlow`, `TrendLine`, `TrendSlopeLookback`, `TrendSlopeAtr`, `RegimeMemory`, `PullbackMax`, `MinPullback`, `CloseInRange`, `MinBarRangeAtr`, `MinLegAtr`, `MinBarsBetween`, `TriggerLife`, `TriggerOffsetTicks`, `TickSize`).
- Produces: `NinjaTrader.NinjaScript.Indicators.BreakBoxVision` with `Values[0] = eF`, `Values[1] = eS`, `Values[2] = eT`; `private int BarSeconds()`; `private void BuildConfigs()`; fields `_atr`, `_eF`, `_eS`, `_eT`, `_cloudCfg`, `_barSec`.

- [ ] **Step 1: Write the failing test**

The gate for an NT8 file is `scripts/check.sh`. Put Vision in it first, so the gate fails until the file exists:

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" \
  && sed -i 's/^FILES=(\(.*\))$/FILES=(\1 BreakBoxVision)/' scripts/check.sh \
  && sed -i 's/all five files/all NT8 files/g; s/All five files/All NT8 files/g' scripts/check.sh \
  && grep -n '^FILES=' scripts/check.sh
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -3`
Expected: FAIL with `grep: /home/javlo/Code Projects/main-project/projects/Trading/BreakBox/ninjascript/BreakBoxVision.cs: No such file or directory`

- [ ] **Step 3: Write minimal implementation**

Create `ninjascript/BreakBoxVision.cs`:

```csharp
// BreakBoxVision.cs — the CALIBRATION indicator. It trades NOTHING: no orders,
// no account, no bracket, no governor. Put it on the chart, look at it, and
// decide whether the model describes the tape in front of you.
//
// IT IS NOT A MIRROR OF THE RUNNING STRATEGY AND MUST NEVER BE READ AS ONE.
// It builds its OWN BbCloudState / BbEngineState from its OWN parameters. Set
// its dials to the strategy's by hand; if they differ, the picture and the
// trades differ — visibly, on purpose. A "mirror" is the worse design: the day
// it silently drifts out of sync you are tuning against a picture of a strategy
// that does not exist, and nothing on the chart tells you. Here a disagreement
// SHOWS.
//
// SOLE OWNER OF BarBrushes (spec §5.3, §12). The strategy never writes a bar
// brush. Two components painting the same pixels on the same chart is a bug
// that renders as a glitch and debugs as a mystery.
//
// SAME MATHS AS THE ENGINE. The EMAs and the ATR are BreakBoxCore.Ema and
// BreakBoxCore.WilderAtr, fed here bar by bar — NOT NT8's EMA()/ATR() wrappers,
// whose seeding differs. The entire value of the picture is that the numbers it
// draws are the numbers the engine reads.
//
// NO HORIZON IN BARS ON THE PARAMETER SURFACE (spec §8). Every dial is seconds
// and is converted once, in BuildConfigs(), against a bar size measured at
// DataLoaded. That is what lets the same parameter set be dropped on a 15s or a
// 1m chart without lying.
//
// CHART: MNQ or NQ, 30-second bars, FULL ETH session template, NT8 time zone
// US Eastern.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;   // REQUIRED for Draw.* — nt8c won't miss it, F5 will
using BreakBoxCore;                           // per-file nt8c check reports CS0246 here: FALSE POSITIVE
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class BreakBoxVision : Indicator
    {
        #region Fields

        private WilderAtr _atr;
        private Ema _eF, _eS, _eT;
        private BbCloudConfig _cloudCfg;
        private int _barSec = BbScale.FallbackSeconds;

        // The ETH open, ET. Not a parameter: it anchors the trading day for the
        // box engine and nothing about this picture is worth a dial for it.
        private const int SessionOpenHhmm = 1800;

        #endregion

        #region Lifecycle

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "BreakBoxVision";
                Description = "Calibration view of the BreakBox cloud and box engines. Trades nothing. "
                    + "Instantiates its OWN state from its OWN parameters — it is NOT a mirror of the strategy.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                IsSuspendedWhileInactive = true;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;

                AddPlot(new Stroke(Brushes.DeepSkyBlue, 1), PlotStyle.Line, "RibbonFast");
                AddPlot(new Stroke(Brushes.SteelBlue, 1), PlotStyle.Line, "RibbonSlow");
                AddPlot(new Stroke(Brushes.Silver, 2), PlotStyle.Line, "TrendLine");

                // ---- Cloud, spec §5.4. Seconds, never bars.
                RibbonFastSec = 300;
                RibbonSlowSec = 690;
                TrendLineSec = 1560;
                TrendSlopeSec = 300;
                TrendSlopeAtr = 0.15;
                RegimeMemorySec = 900;
                PullbackMaxSec = 600;
                MinPullbackSec = 30;
                CloseInRangeMin = 0.60;
                MinBarRangeAtr = 0.20;
                MinLegAtr = 0.35;
                MinBarsBetweenSec = 180;
                TriggerLifeSec = 120;
                TriggerOffsetTicks = 1;
                AtrPeriod = 14;
            }
            else if (State == State.DataLoaded)
            {
                _barSec = BarSeconds();
                BuildConfigs();

                _atr = new WilderAtr(AtrPeriod);
                _eF = new Ema(_cloudCfg.RibbonFast);
                _eS = new Ema(_cloudCfg.RibbonSlow);
                _eT = new Ema(_cloudCfg.TrendLine);

                Print(string.Format(CultureInfo.InvariantCulture,
                    "BreakBoxVision: bar = {0}s -> ribbon {1}/{2}, trend {3} bars. "
                    + "These are VISION's parameters, not the strategy's.",
                    _barSec, _cloudCfg.RibbonFast, _cloudCfg.RibbonSlow, _cloudCfg.TrendLine));
            }
        }

        // Seconds per bar. Time-based series are exact; tick/volume/range series
        // are ESTIMATED from the loaded history and the estimate is printed,
        // because the model is calibrated for time bars and a silent guess is
        // how "escala sola" turns into "escaló mal" (spec §8).
        //
        // The estimate itself is BbScale.EstimateBarSeconds — the strategy's
        // median, its sample floor, its sub-second guard. A second hand-rolled
        // copy here would be a chart that scales by slightly different rules
        // than the engine it claims to be drawing, and the day the two disagree
        // you would go looking for the bug in the model.
        private int BarSeconds()
        {
            int v = BarsPeriod.Value < 1 ? 1 : BarsPeriod.Value;
            switch (BarsPeriod.BarsPeriodType)
            {
                case BarsPeriodType.Second: return v;
                case BarsPeriodType.Minute: return v * 60;
                case BarsPeriodType.Day:    return v * 86400;
            }

            int n = Bars != null ? Bars.Count : 0;
            int want = n - 1;
            if (want > 5000)
                want = 5000;                    // one session of 150-tick bars is already plenty
            if (want >= BbScale.MinEstimateSamples)
            {
                double[] gaps = new double[want];
                int first = n - want;
                for (int i = 0; i < want; i++)
                    gaps[i] = (Bars.GetTime(first + i) - Bars.GetTime(first + i - 1)).TotalSeconds;

                int est = BbScale.EstimateBarSeconds(gaps, want);
                if (est > 0)
                {
                    Print("BreakBoxVision: non-time series — bar ~= " + est + "s (estimated over "
                          + want + " gaps).");
                    return est;
                }
            }

            Print("BreakBoxVision: " + BarsPeriod.BarsPeriodType + " series with " + n
                  + " bars loaded — too little history to estimate the bar size. Falling back to "
                  + BbScale.FallbackSeconds + "s, so EVERY seconds-based dial on this chart is a guess.");
            return BbScale.FallbackSeconds;
        }

        // Seconds -> bars, in ONE place (spec §8), through the ONE scaler
        // (Phase 1, Task 1). Called from DataLoaded only: this indicator has no
        // panel and nothing else rebuilds it.
        private void BuildConfigs()
        {
            int b = _barSec;                    // BbScale.Bars floors a nonsense bar size itself

            _cloudCfg = new BbCloudConfig();
            _cloudCfg.TickSize = TickSize;
            _cloudCfg.RibbonFast = BbScale.Bars(RibbonFastSec, b, 2);
            _cloudCfg.RibbonSlow = BbScale.Bars(RibbonSlowSec, b, 2);
            _cloudCfg.TrendLine = BbScale.Bars(TrendLineSec, b, 2);
            _cloudCfg.TrendSlopeLookback = BbScale.Bars(TrendSlopeSec, b, 2);
            _cloudCfg.TrendSlopeAtr = TrendSlopeAtr;
            _cloudCfg.RegimeMemory = BbScale.Bars(RegimeMemorySec, b, 2);
            _cloudCfg.PullbackMax = BbScale.Bars(PullbackMaxSec, b, 2);
            _cloudCfg.MinPullback = BbScale.Bars(MinPullbackSec, b, 1);
            _cloudCfg.CloseInRange = CloseInRangeMin;
            _cloudCfg.MinBarRangeAtr = MinBarRangeAtr;
            _cloudCfg.MinLegAtr = MinLegAtr;
            _cloudCfg.MinBarsBetween = BbScale.Bars(MinBarsBetweenSec, b, 1);
            _cloudCfg.TriggerLife = BbScale.Bars(TriggerLifeSec, b, 1);
            _cloudCfg.TriggerOffsetTicks = TriggerOffsetTicks;
        }

        #endregion

        #region Bar loop

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1)
                return;

            BbBar bar = ToBar();

            // Update FIRST, exactly like the engine's step 0 (spec §5.2): every
            // gate reads post-update values, and `close > eF` means something
            // materially different under the other convention.
            _atr.Update(bar);
            _eF.Update(bar.Close);
            _eS.Update(bar.Close);
            _eT.Update(bar.Close);

            Values[0][0] = _eF.Value;
            Values[1][0] = _eS.Value;
            Values[2][0] = _eT.Value;
        }

        private BbBar ToBar()
        {
            BbBar b;
            b.Time = Time[0];
            b.Open = Open[0];
            b.High = High[0];
            b.Low = Low[0];
            b.Close = Close[0];
            b.Volume = Volume[0];
            return b;
        }

        #endregion

        #region Parameters

        [NinjaScriptProperty, Range(30, 7200)]
        [Display(Name = "Ribbon fast (sec)", Order = 1, GroupName = "01. Cloud")]
        public int RibbonFastSec { get; set; }

        [NinjaScriptProperty, Range(30, 7200)]
        [Display(Name = "Ribbon slow (sec)", Order = 2, GroupName = "01. Cloud")]
        public int RibbonSlowSec { get; set; }

        [NinjaScriptProperty, Range(30, 14400)]
        [Display(Name = "Trend line (sec)", Order = 3, GroupName = "01. Cloud")]
        public int TrendLineSec { get; set; }

        [NinjaScriptProperty, Range(30, 3600)]
        [Display(Name = "Trend slope lookback (sec)", Order = 4, GroupName = "01. Cloud")]
        public int TrendSlopeSec { get; set; }

        [NinjaScriptProperty, Range(0.0, 5.0)]
        [Display(Name = "Trend slope (ATR)", Order = 5, GroupName = "01. Cloud")]
        public double TrendSlopeAtr { get; set; }

        [NinjaScriptProperty, Range(30, 7200)]
        [Display(Name = "Regime memory (sec)", Order = 6, GroupName = "01. Cloud")]
        public int RegimeMemorySec { get; set; }

        [NinjaScriptProperty, Range(30, 7200)]
        [Display(Name = "Pullback max (sec)", Order = 7, GroupName = "01. Cloud")]
        public int PullbackMaxSec { get; set; }

        [NinjaScriptProperty, Range(0, 3600)]
        [Display(Name = "Min pullback (sec)", Order = 8, GroupName = "01. Cloud")]
        public int MinPullbackSec { get; set; }

        [NinjaScriptProperty, Range(0.0, 1.0)]
        [Display(Name = "Close in range", Description = "Gate (c): both reference candles were wickless (1.00)", Order = 9, GroupName = "01. Cloud")]
        public double CloseInRangeMin { get; set; }

        [NinjaScriptProperty, Range(0.0, 5.0)]
        [Display(Name = "Min bar range (ATR)", Order = 10, GroupName = "01. Cloud")]
        public double MinBarRangeAtr { get; set; }

        [NinjaScriptProperty, Range(0.0, 5.0)]
        [Display(Name = "Min leg (ATR)", Order = 11, GroupName = "01. Cloud")]
        public double MinLegAtr { get; set; }

        [NinjaScriptProperty, Range(0, 7200)]
        [Display(Name = "Min bars between (sec)", Order = 12, GroupName = "01. Cloud")]
        public int MinBarsBetweenSec { get; set; }

        [NinjaScriptProperty, Range(30, 3600)]
        [Display(Name = "Trigger life (sec)", Order = 13, GroupName = "01. Cloud")]
        public int TriggerLifeSec { get; set; }

        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Trigger offset (ticks)", Order = 14, GroupName = "01. Cloud")]
        public int TriggerOffsetTicks { get; set; }

        [NinjaScriptProperty, Range(2, 500)]
        [Display(Name = "ATR period", Order = 15, GroupName = "01. Cloud")]
        public int AtrPeriod { get; set; }

        #endregion
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -4`
Expected: `ALL PASS (<n> checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxVision.cs scripts/check.sh && git commit -m "feat(vision): BreakBoxVision skeleton — ribbon/trend plots on the engine's own EMAs

Indicator, trades nothing. Its own parameters, converted through the SAME
BbScale the strategy uses, and BreakBoxCore.Ema/WilderAtr rather than NT8's
wrappers so the drawn numbers are the read numbers. Explicitly a calibration
tool, not a mirror (spec §12).

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 82: Vision drives its OWN `BbCloud`

**Files:**
- Modify: `projects/Trading/BreakBox/ninjascript/BreakBoxVision.cs` (fields region, `State.DataLoaded`, `OnBarUpdate`)
- Test: `projects/Trading/BreakBox/scripts/check.sh`

**Interfaces:**
- Consumes: `BbCloudState` (with `RegimeLatched`, `Armed`, `Ext`, `AgeBars`, `Gate`, `SlopeBuf` sized at DataLoaded), `BbCloud(BbCloudConfig, BbCloudState)`, `BbCloud.OnBar(BbBar, int secs, double eF, double eS, double eT, double atr, bool atrWarm, bool canTrade, bool positioned)`, `BbCloud.OnEntryFilled()`.
- Produces: fields `_cloudSt`, `_cloud`, `_lastAction` (the `BbAction` returned on this bar) — Tasks 83/84 read them.

- [ ] **Step 1: Write the failing check**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -c "_cloud.OnBar" ninjascript/BreakBoxVision.cs`
Expected: `0` — Vision draws EMAs but runs no engine, so nothing on the chart can disagree with the strategy yet.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -q "_cloud.OnBar" ninjascript/BreakBoxVision.cs && echo PRESENT || echo ABSENT`
Expected: `ABSENT`

- [ ] **Step 3: Write minimal implementation**

Add to the `#region Fields` block, after `private BbCloudConfig _cloudCfg;`:

```csharp
        private BbCloudState _cloudSt;
        private BbCloud _cloud;
        private BbAction _lastAction;
```

In `State.DataLoaded`, after the `_eT = new Ema(...)` line:

```csharp
                // Vision's own state object. NOT the strategy's — nothing is
                // shared, nothing is read across processes, and the two are
                // expected to agree only because their dials were set to agree.
                _cloudSt = new BbCloudState();
                _cloudSt.SlopeBuf = new double[_cloudCfg.TrendSlopeLookback + 1];
                _cloud = new BbCloud(_cloudCfg, _cloudSt);
```

In `OnBarUpdate`, after the three `Values[...][0] = ...` assignments:

```csharp
            int secs = Time[0].Hour * 3600 + Time[0].Minute * 60 + Time[0].Second;

            // canTrade = true, positioned = false, ALWAYS. Vision has no
            // lockout, no window and no position, and hiding triggers behind a
            // simulated governor is how a calibration view starts explaining
            // away the very bars you are trying to count.
            _lastAction = _cloud.OnBar(bar, secs, _eF.Value, _eS.Value, _eT.Value,
                                       _atr.Value, _atr.IsWarm, true, false);

            // Assume every trigger filled. Vision has no order layer, so the
            // alternative is a token that stays minted forever and a chart that
            // paints a gold candle on every subsequent bar of the same pullback.
            if (_lastAction.Fire)
                _cloud.OnEntryFilled();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -q "_cloud.OnBar" ninjascript/BreakBoxVision.cs && bash scripts/check.sh 2>&1 | tail -3`
Expected: `ALL PASS (<n> checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxVision.cs && git commit -m "feat(vision): run the cloud engine from Vision's own BbCloudState

canTrade always true, positioned always false, every trigger treated as
filled: a calibration view counts what the model sees, not what a governor
would have allowed.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 83: the cloud — a regime-tinted region between eF and eS

**Files:**
- Modify: `projects/Trading/BreakBox/ninjascript/BreakBoxVision.cs` (fields, `OnBarUpdate`, new `#region Painting`)
- Test: `projects/Trading/BreakBox/scripts/check.sh`

**Interfaces:**
- Consumes: `Draw.Region(NinjaScriptBase, string tag, int startBarsAgo, int endBarsAgo, ISeries<double> series1, ISeries<double> series2, Brush outlineBrush, Brush areaBrush, int areaOpacity)` (verified in NT8 `DrawingTools/@Region.cs:400`), `_cloudSt.RegimeLatched`.
- Produces: `private void PaintCloud()`, `private void DrawTag(object)`, fields `_drawTags`, `_tagSeq`, `_segStartBar`, `_segRegime`, `_segTag`.

- [ ] **Step 1: Write the failing check**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -q "Draw.Region" ninjascript/BreakBoxVision.cs && echo PRESENT || echo ABSENT`
Expected: `ABSENT` — the two ribbon lines are drawn but the space between them is empty, which is the single most visible difference from the reference frames.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -c "Draw.Region\|PaintCloud" ninjascript/BreakBoxVision.cs`
Expected: `0`

- [ ] **Step 3: Write minimal implementation**

Add to `#region Fields`:

```csharp
        // The cloud is drawn as ONE region per regime run, not one per bar: a
        // 30s chart is ~780 bars a session and 780 draw objects is a memory
        // leak with a chart attached.
        private readonly List<string> _drawTags = new List<string>();
        private int _tagSeq;
        private int _segStartBar = -1;
        private int _segRegime = int.MinValue;
        private string _segTag = "";
        private const int MaxDrawTags = 4000;

        private static readonly Brush CloudUp = Brushes.MediumSeaGreen;
        private static readonly Brush CloudDn = Brushes.IndianRed;
        private static readonly Brush CloudFlat = Brushes.DimGray;
```

Add a new region before `#region Parameters`:

```csharp
        #region Painting

        // The cloud: the band between the two ribbon EMAs, tinted by the
        // LATCHED regime (spec §5.2 step 2). Latched, not instantaneous — the
        // instantaneous regime zeroes on exactly the pullbacks the engine is
        // waiting for, so tinting by it would strobe grey on every setup and
        // show a market the engine does not believe it is in.
        private void PaintCloud()
        {
            int regime = _cloudSt.RegimeLatched;
            if (regime != _segRegime || _segStartBar < 0)
            {
                _segRegime = regime;
                _segStartBar = CurrentBar;
                _segTag = "bbv_cloud_" + (_tagSeq++);
            }

            // A region needs width. On the bar a segment opens there is none.
            int startBarsAgo = CurrentBar - _segStartBar;
            if (startBarsAgo < 1)
                return;

            Brush area = regime > 0 ? CloudUp : (regime < 0 ? CloudDn : CloudFlat);
            DrawTag(Draw.Region(this, _segTag, startBarsAgo, 0, Values[0], Values[1], null, area, 20));
        }

        // Same tag-ring discipline as the strategy (BreakBoxStrategy.cs:824) and
        // for the same reason. The last-tag check keeps a region that is
        // redrawn on every bar of its segment from filling the ring with one
        // repeated name.
        private void DrawTag(object drawn)
        {
            var d = drawn as DrawingTool;
            if (d == null)
                return;
            if (_drawTags.Count > 0 && _drawTags[_drawTags.Count - 1] == d.Tag)
                return;
            _drawTags.Add(d.Tag);
            while (_drawTags.Count > MaxDrawTags)
            {
                RemoveDrawObject(_drawTags[0]);
                _drawTags.RemoveAt(0);
            }
        }

        #endregion

```

In `OnBarUpdate`, after the `if (_lastAction.Fire) _cloud.OnEntryFilled();` block:

```csharp
            PaintCloud();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -3`
Expected: `ALL PASS (<n> checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxVision.cs && git commit -m "feat(vision): draw the cloud as a regime-tinted region, one object per regime run

Tinted by the LATCHED regime: the instantaneous one zeroes on exactly the
pullbacks the engine is waiting for and would strobe grey on every setup.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 84: gold and dim-gold signal candles — Vision is the sole owner of `BarBrushes`

**Files:**
- Modify: `projects/Trading/BreakBox/ninjascript/BreakBoxVision.cs` (fields, `OnBarUpdate`, `#region Painting`)
- Test: `projects/Trading/BreakBox/scripts/check.sh` (the gate maths itself is pinned by Task 80, in Phase 1)

**Interfaces:**
- Consumes: `BbMath.CloseInRange(BbBar, int)` (Task 80, Phase 1 — the same function the cloud engine's gate (c) calls), `_lastAction.Fire`, `_cloudSt.RegimeLatched`.
- Produces: `private void PaintSignalBar(BbBar bar)`.

- [ ] **Step 1: Write the failing check**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -q "BarBrushes" ninjascript/BreakBoxVision.cs && echo PRESENT || echo ABSENT`
Expected: `ABSENT` — and confirm nobody else owns them: `grep -rn "BarBrushes" ninjascript/` must print nothing at all.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -rn "BarBrushes" ninjascript/ | wc -l`
Expected: `0`

- [ ] **Step 3: Write minimal implementation**

Add to `#region Fields`, under the cloud brushes:

```csharp
        // Gold = every gate passed. Dim gold = the BAR looked right and the
        // CONTEXT did not. The dim bars are the free diagnostic: a session full
        // of them means the candle gates are fine and the token/regime/leg
        // gates are what is starving the strategy (spec §5.3).
        private static readonly Brush GoldBrush = Brushes.Gold;
        private static readonly Brush DimGoldBrush = Brushes.DarkGoldenrod;
```

Add to `#region Painting`, after `PaintCloud()`:

```csharp
        // VISION IS THE SOLE OWNER OF BarBrushes (spec §5.3, §12). The strategy
        // never writes one. Two components painting the same pixels on the same
        // chart is a bug that renders as a flicker and debugs as a mystery.
        //
        // Gold comes from the engine (it fired). Dim gold is recomputed HERE
        // from gates b/c/d, which are pure functions of this one bar and need
        // no engine state — so the engine keeps no reporting surface it would
        // otherwise have to maintain. The ratio itself comes from
        // BbMath.CloseInRange, the same function gate (c) uses, so the painted
        // bar and the traded bar cannot drift apart.
        private void PaintSignalBar(BbBar bar)
        {
            if (_lastAction.Fire)
            {
                BarBrushes[0] = GoldBrush;
                return;
            }

            if (!_atr.IsWarm)
                return;

            // A bar gate is only meaningful against a direction, and the
            // direction is the latched regime. With no regime there is nothing
            // this bar could have been the signal for.
            int dir = _cloudSt.RegimeLatched;
            if (dir == 0)
                return;

            bool body = dir > 0 ? bar.Close > bar.Open : bar.Close < bar.Open;
            // NaN on a flat bar makes this comparison false — the gate fails
            // closed, which is the behaviour Task 80 pins.
            bool shape = BbMath.CloseInRange(bar, dir) >= _cloudCfg.CloseInRange;
            bool range = (bar.High - bar.Low) >= _cloudCfg.MinBarRangeAtr * _atr.Value;

            if (body && shape && range)
                BarBrushes[0] = DimGoldBrush;
        }
```

In `OnBarUpdate`, replace the single `PaintCloud();` line with:

```csharp
            PaintCloud();
            PaintSignalBar(bar);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -3 && grep -rln "BarBrushes" ninjascript/`
Expected: `compiles clean`, and the grep lists `ninjascript/BreakBoxVision.cs` and nothing else

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxVision.cs && git commit -m "feat(vision): gold / dim-gold signal candles, Vision the sole owner of BarBrushes

Gold = the engine fired. Dim gold = bar gates b/c/d passed and context did
not, recomputed locally from the SHARED BbMath.CloseInRange so the painted
bar and the traded bar cannot drift.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 85: sealed accumulation boxes as white rectangles

**Files:**
- Modify: `projects/Trading/BreakBox/ninjascript/BreakBoxVision.cs` (fields, `State.DataLoaded`, `BuildConfigs`, `OnBarUpdate`, `#region Painting`, `#region Parameters`)
- Test: `projects/Trading/BreakBox/scripts/check.sh`

**Interfaces:**
- Consumes: the eleven `BbConfig` box fields of spec §6.3, **which are in bars**, and Vision must reproduce Phase 3's `BuildConfigs()` (Task 48) split exactly — the same three dials convert and the same eight pass through, or the picture is running a different box engine than the strategy:
  - **Converted** (a horizon in seconds → bars, floors quoted from T48): `BoxLookback = BbScale.Bars(BoxLookbackSec, _barSec, 2)`, `BoxMaxAge = BbScale.Bars(BoxMaxAgeSec, _barSec, 2)`, `BoxArmCooldown = BbScale.Bars(BoxArmCooldownSec, _barSec, 1)`.
  - **Passed straight through**, because they are not horizons: `BoxMinBars` (T48: "a confirmation COUNT, not a horizon"), `BoxRangePctile`, `BoxSampleN`, `BoxMeanSamples`, `BoxValidLo`, `BoxValidHi`, `BoxDeadAtr`, `BoxArmsPerEdge`.
- Consumes: `BbEngineState`, `BbEngine(BbConfig, BbEngineState)`, `BbEngine.OnBar(BbBar, int secs, DateTime sessionDate, double atr, bool atrWarm, bool canTrade, bool positioned)`, `BbEngine.OnEntryFilled()`, `BbEngine.Box` → `BbBox { Id, High, Low, SealedAt }` (Task 40's rewritten box: the v1 `AnchorStart`/`AnchorEnd` pair is gone and `SealedAt` — the bar that froze the edges — is what is left to anchor a rectangle to).
- Produces: `private void PaintBox()`, `private DateTime SessionDateOf(DateTime, int)`, fields `_boxCfg`, `_boxSt`, `_boxEngine`, `_lastDrawnBoxId`.

- [ ] **Step 1: Write the failing check**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -q "PaintBox" ninjascript/BreakBoxVision.cs && echo PRESENT || echo ABSENT`
Expected: `ABSENT` — the micro-accumulation rectangle is the object the reference actually trades (spec §1) and the chart does not show it yet.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -c "BbEngine\|PaintBox" ninjascript/BreakBoxVision.cs`
Expected: `0`

- [ ] **Step 3: Write minimal implementation**

Add to `#region Fields`:

```csharp
        private BbConfig _boxCfg;
        private BbEngineState _boxSt;
        private BbEngine _boxEngine;
        private int _lastDrawnBoxId = -1;

        // White, like the reference. The cyan 4H rectangle in those frames is
        // distant context and is NOT this object (spec §1).
        private static readonly Brush BoxBrush = Brushes.White;
```

In `BuildConfigs()`, after the `_cloudCfg.TriggerOffsetTicks = TriggerOffsetTicks;` line:

```csharp
            _boxCfg = new BbConfig();
            _boxCfg.TickSize = TickSize;
            _boxCfg.AtrPeriod = AtrPeriod;
            _boxCfg.BoxLookback = BbScale.Bars(BoxLookbackSec, b, 2);
            _boxCfg.BoxMinBars = BoxMinBars;
            _boxCfg.BoxRangePctile = BoxRangePctile;
            _boxCfg.BoxSampleN = BoxSampleN;
            _boxCfg.BoxMeanSamples = BoxMeanSamples;
            _boxCfg.BoxValidLo = BoxValidLo;
            _boxCfg.BoxValidHi = BoxValidHi;
            _boxCfg.BoxDeadAtr = BoxDeadAtr;
            _boxCfg.BoxMaxAge = BbScale.Bars(BoxMaxAgeSec, b, 2);
            _boxCfg.BoxArmsPerEdge = BoxArmsPerEdge;
            _boxCfg.BoxArmCooldown = BbScale.Bars(BoxArmCooldownSec, b, 1);
```

In `State.DataLoaded`, after `_cloud = new BbCloud(_cloudCfg, _cloudSt);`:

```csharp
                _boxSt = new BbEngineState();
                _boxEngine = new BbEngine(_boxCfg, _boxSt);
```

In `OnBarUpdate`, between the cloud call and `PaintCloud();`:

```csharp
            // BOTH engines run, unconditionally. §4.1 arbitration is the
            // STRATEGY's job; a calibration view that hid the box because the
            // cloud armed first would hide exactly the trades you are trying to
            // account for.
            DateTime sessionDate = SessionDateOf(Time[0], secs);
            BbAction boxAction = _boxEngine.OnBar(bar, secs, sessionDate, _atr.Value, _atr.IsWarm, true, false);
            if (boxAction.Fire)
                _boxEngine.OnEntryFilled();
```

and after `PaintSignalBar(bar);`:

```csharp
            PaintBox();
```

Add to `#region Bar loop`, after `ToBar()`:

```csharp
        // Which trading day does this bar belong to? The ETH session opens at
        // 18:00 ET, so an 18:30 bar belongs to the NEXT calendar day's session.
        // Same rule as the strategy (BreakBoxStrategy.cs:416) — a different one
        // here would roll the box engine's daily counters on a different bar
        // and the picture would disagree for a reason that has nothing to do
        // with the model.
        private DateTime SessionDateOf(DateTime t, int secs)
        {
            return secs >= BbMath.HhmmToSecs(SessionOpenHhmm) ? t.Date.AddDays(1) : t.Date;
        }
```

Add to `#region Painting`:

```csharp
        // One rectangle per SEALED box, drawn once when its Id changes. A box's
        // edges never move after the seal (spec §6.1), so redrawing it every
        // bar would buy nothing and cost 780 draw objects a session.
        //
        // Anchored on `SealedAt`, the bar that froze the edges — the same
        // anchor the strategy's own DrawBox uses. Task 40's box has no
        // AnchorStart: the candidate window that produced it is not part of the
        // object, and the rectangle you want to see is the one the engine is
        // trading, not the samples it measured.
        private void PaintBox()
        {
            BbBox box = _boxEngine.Box;
            if (box == null || box.Id == _lastDrawnBoxId)
                return;
            _lastDrawnBoxId = box.Id;

            DrawTag(Draw.Rectangle(this, "bbv_box_" + box.Id, false,
                                   box.SealedAt, box.Low, Time[0], box.High,
                                   BoxBrush, BoxBrush, 6));
        }
```

Add to `#region Parameters`:

```csharp
        [NinjaScriptProperty, Range(30, 7200)]
        [Display(Name = "Box lookback (sec)", Description = "210 = the measured ~7-bar white rectangle at 30s", Order = 1, GroupName = "02. Box")]
        public int BoxLookbackSec { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Box min bars", Order = 2, GroupName = "02. Box")]
        public int BoxMinBars { get; set; }

        // double, and [Range(1.0, 99.0)], because that is exactly how Task 48
        // types it on the strategy. Same dial, same resolution: an int here
        // would quietly refuse the 35.5 you set on the strategy and Task 87
        // asks you to keep the two surfaces identical dial-for-dial.
        [NinjaScriptProperty, Range(1.0, 99.0)]
        [Display(Name = "Box range percentile", Order = 3, GroupName = "02. Box")]
        public double BoxRangePctile { get; set; }

        [NinjaScriptProperty, Range(20, 5000)]
        [Display(Name = "Box sample N", Order = 4, GroupName = "02. Box")]
        public int BoxSampleN { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Box mean samples", Description = "Cold start: no box engine until this many have sealed", Order = 5, GroupName = "02. Box")]
        public int BoxMeanSamples { get; set; }

        [NinjaScriptProperty, Range(0.0, 10.0)]
        [Display(Name = "Box valid lo", Order = 6, GroupName = "02. Box")]
        public double BoxValidLo { get; set; }

        [NinjaScriptProperty, Range(0.0, 50.0)]
        [Display(Name = "Box valid hi", Order = 7, GroupName = "02. Box")]
        public double BoxValidHi { get; set; }

        [NinjaScriptProperty, Range(0.0, 10.0)]
        [Display(Name = "Box dead (ATR)", Order = 8, GroupName = "02. Box")]
        public double BoxDeadAtr { get; set; }

        [NinjaScriptProperty, Range(60, 86400)]
        [Display(Name = "Box max age (sec)", Order = 9, GroupName = "02. Box")]
        public int BoxMaxAgeSec { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Box arms per edge", Order = 10, GroupName = "02. Box")]
        public int BoxArmsPerEdge { get; set; }

        [NinjaScriptProperty, Range(0, 7200)]
        [Display(Name = "Box arm cooldown (sec)", Order = 11, GroupName = "02. Box")]
        public int BoxArmCooldownSec { get; set; }
```

And their defaults, in `State.SetDefaults` after `AtrPeriod = 14;`:

```csharp
                // ---- Box, spec §6.3
                BoxLookbackSec = 210;
                BoxMinBars = 2;
                BoxRangePctile = 35.0;
                BoxSampleN = 200;
                BoxMeanSamples = 20;
                BoxValidLo = 0.4;
                BoxValidHi = 2.5;
                BoxDeadAtr = 0.5;
                BoxMaxAgeSec = 1800;
                BoxArmsPerEdge = 2;
                BoxArmCooldownSec = 180;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -3`
Expected: `ALL PASS (<n> checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxVision.cs && git commit -m "feat(vision): sealed accumulation boxes as white rectangles

Both engines run unconditionally — §4.1 arbitration belongs to the strategy,
and a view that hid the box would hide the trades being accounted for.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 86: entry / exit markers read from the §10 JSONL

**Files:**
- Modify: `projects/Trading/BreakBox/ninjascript/BreakBoxVision.cs` (fields, `State.DataLoaded`, `OnBarUpdate`, `#region Painting`)
- Test: `projects/Trading/BreakBox/scripts/check.sh`

**Interfaces:**
- Consumes: `BbTradeRecord { Ts, Dir, Entry, Exit, Qty, R, Pnl, Engine, ExitReason, CfgHash }`, `BbHistory.TryParse(string, out BbTradeRecord)`, the §10 file layout `<UserDataDir>/BreakBox/history-<instrument>-<account>[-replay].jsonl`.
- Produces: `private void LoadHistory()`, `private void DrawMarkers(DateTime barTime)`, fields `_history`, `_histIdx`, `_firstBarTime`.

- [ ] **Step 1: Write the failing check**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -q "BbHistory.TryParse" ninjascript/BreakBoxVision.cs && echo PRESENT || echo ABSENT`
Expected: `ABSENT` — the chart shows the model but not a single trade the strategy actually took, which is half of the side-by-side comparison Task 87 depends on.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && grep -c "ArrowUp\|ArrowDown" ninjascript/BreakBoxVision.cs`
Expected: `0`

- [ ] **Step 3: Write minimal implementation**

Add to `#region Fields`:

```csharp
        // Markers come from the history FILE (spec §10), never from execution
        // events. No coupling to a running strategy: this works on a chart with
        // nothing attached, and on last week's session.
        private readonly List<BbTradeRecord> _history = new List<BbTradeRecord>();
        private int _histIdx;
        private DateTime _firstBarTime = DateTime.MinValue;

        private static readonly Brush EntryBrush = Brushes.DodgerBlue;    // blue up-arrow, per §2.2
        private static readonly Brush ExitBrush = Brushes.Magenta;        // magenta down-arrow
```

In `State.DataLoaded`, after the `_boxEngine = new BbEngine(...)` line:

```csharp
                LoadHistory();
```

Add to `#region Painting`:

```csharp
        // Reads every history file for this instrument — live and -replay, all
        // accounts. Vision does not know which account the strategy ran on, and
        // asking for one dial per file name is a worse trade than showing them
        // all: an unexpected marker is a question, a missing one is silence.
        private void LoadHistory()
        {
            _history.Clear();
            _histIdx = 0;

            string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "BreakBox");
            if (!Directory.Exists(dir))
            {
                Print("BreakBoxVision: no history directory at " + dir + " — no markers to draw.");
                return;
            }

            string pattern = "history-" + Instrument.MasterInstrument.Name + "-*.jsonl";
            string[] files = Directory.GetFiles(dir, pattern);
            if (files.Length == 0)
            {
                Print("BreakBoxVision: no file matched " + pattern + " in " + dir + " — no markers to draw.");
                return;
            }

            for (int f = 0; f < files.Length; f++)
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(files[f]);
                }
                catch (IOException ex)
                {
                    // The strategy appends to this file while we read it. A
                    // locked file costs markers, never the indicator.
                    Print("BreakBoxVision: could not read " + files[f] + " (" + ex.Message + ")");
                    continue;
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    BbTradeRecord r;
                    if (BbHistory.TryParse(lines[i], out r))
                        _history.Add(r);
                }
            }

            // Files are appended in time order, but there are several of them.
            _history.Sort(delegate (BbTradeRecord a, BbTradeRecord b) { return a.Ts.CompareTo(b.Ts); });
            Print("BreakBoxVision: " + _history.Count + " history rows from " + files.Length + " file(s).");
        }

        // Drains every record up to this bar's close and marks it. Trades older
        // than the loaded chart are DISCARDED, not stacked on bar 0 — a pile of
        // forty arrows on the leftmost bar is worse than no arrows at all.
        //
        // The record carries one timestamp, so the exit arrow lands on the
        // ENTRY's bar. On the reference's 30-60 second holds that is the same
        // bar column anyway (spec §2); on a long hold it will be visibly wrong,
        // and that is the honest failure for a record that has no exit time.
        private void DrawMarkers(DateTime barTime)
        {
            while (_histIdx < _history.Count && _history[_histIdx].Ts <= barTime)
            {
                BbTradeRecord r = _history[_histIdx++];
                if (r.Ts < _firstBarTime)
                    continue;

                string id = r.Ts.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_" + (_tagSeq++);
                DrawTag(Draw.ArrowUp(this, "bbv_in_" + id, false, 0, r.Entry, EntryBrush));
                if (r.Exit > 0.0)
                    DrawTag(Draw.ArrowDown(this, "bbv_out_" + id, false, 0, r.Exit, ExitBrush));
            }
        }
```

In `OnBarUpdate`, immediately after `if (CurrentBar < 1) return;`:

```csharp
            if (_firstBarTime == DateTime.MinValue)
                _firstBarTime = Time[0];
```

and after `PaintBox();`:

```csharp
            DrawMarkers(Time[0]);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -3`
Expected: `ALL PASS (<n> checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && git add ninjascript/BreakBoxVision.cs && git commit -m "feat(vision): entry/exit markers read from the history JSONL

Blue up-arrows on entries, magenta down-arrows on exits (§2.2), from the
file rather than from execution events — so the view works on a chart with
no strategy attached, and on last week's session.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 87: deploy to NT8 and calibrate against `reference/` — the afternoon that answers *"no se parece en nada"*

**Files:**
- Deploy: `ninjascript/BreakBoxVision.cs` → `/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Indicators/BreakBoxVision.cs` (the `namespace ...Indicators` decides the folder, not the role)
- Read: `projects/Trading/BreakBox/reference/WhatsApp Image 2026-08-16 at 16.40.50*.jpeg` (4 frames)

**Interfaces:**
- Consumes: everything above.
- Produces: a written list of disagreements between the picture and the reference. Each one is a measurement bug found before it becomes a trading bug — which is the entire point of this phase (spec §12).

- [ ] **Step 1: Deploy the file where Javier runs NinjaTrader**

A NinjaScript task is not done until the file is in `Custom/`. `nt8c` clean is not done, committed is not done.

```bash
cp "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/ninjascript/BreakBoxVision.cs" \
   "/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Indicators/BreakBoxVision.cs"
```

- [ ] **Step 2: The two post-deploy checks**

```bash
find "/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Indicators" \
     "/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Strategies" \
     -name "BreakBox*.cs" -printf "%f\n" | sort | uniq -d
cmp "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox/ninjascript/BreakBoxVision.cs" \
    "/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Indicators/BreakBoxVision.cs"
```

Expected: the `uniq -d` prints NOTHING (a duplicate basename across `Indicators/` and `Strategies/` makes NT8 throw a CS0101 cascade on F5, and the staged nt8c build cannot see it), and `cmp` is silent.

- [ ] **Step 3: Javier — put it on the chart**

1. NinjaScript Editor → **F5**. It must compile with the strategy files already there.
2. New chart: **MNQ 09-26**, **30 Second** bars, **full ETH** session template, NT8 time zone **US Eastern**.
3. Indicators → **BreakBoxVision** → add. Leave every parameter at its default: those defaults are the §5.4 / §6.3 tables, which are what the measurement claims the reference is running.
4. Scroll back over a full RTH session and read the Output window: it prints the bar size it derived and the history row count. If it says `bar = 30s` you are on the chart the model was measured for.

**Say it out loud once:** this indicator is **not** the strategy. It runs its own copy of the engines from its own dials. If you later change a dial on the strategy, change it here too — otherwise the picture is describing a different strategy, and it will not tell you.

- [ ] **Step 4: Compare against the four reference frames, in this order**

Open `reference/WhatsApp Image 2026-08-16 at 16.40.50*.jpeg` next to the chart and check five things. Each is a specific claim from the spec that can be wrong:

| # | What to look at | The claim it tests | If it disagrees |
|---|---|---|---|
| 1 | The **cloud band's thickness and the trend line's distance** from it | ribbon = EMA(10)/EMA(23), trend = EMA(52) — §2, tagged **M** | The EMA measurement is wrong. Nothing downstream is worth tuning until it is right. |
| 2 | **How often the cloud changes tint** | the latched regime + `TrendSlopeAtr = 0.15` — §5.4, tagged **G** | Strobing = the slope gate is too soft. One tint all session = too hard. |
| 3 | **How many gold vs dim-gold bars** per session | the candle gates (b,c,d) vs the context gates — §5.3 | Lots of dim gold, no gold → the token/regime/leg gates are what is starving it. No dim gold at all → `CloseInRange` or `MinBarRangeAtr` is too tight; §5.4 caps `MinBarRangeAtr` at 0.30 because 0.30 already rejects a trade we KNOW was taken. |
| 4 | **The white rectangles**: size (~7 bars) and how often one seals | `BoxLookbackSec = 210` and the §6.1 lifecycle | No rectangles at all for the first while is correct — the box engine is hard-disabled until `BoxMeanSamples = 20` boxes have sealed (§6.1 cold start). Never sealing is a bug. |
| 5 | **Gold bars vs blue arrows** | the strategy and the model agree | Gold where there is no arrow = the strategy refused a trigger the model produced (window, budget, lockout, or a dial that differs from Vision's). Arrow with no gold = the dials differ; check them one by one before suspecting the engine. |

- [ ] **Step 5: Write down what disagreed, then stop**

Record the disagreements as a list — one line each, with the frame and the parameter you suspect. Do **not** start tuning off this. The picture tells you *which measurement to re-check*; the §13 protocol (counter run first, orders disabled, 5-10 Replay sessions, then tune at most three **G** parameters) is what tells you *what to set it to*. Guessing constants and looking at a chart is fitting noise once; guessing constants and looking at P&L is fitting it twice.

Two spec-level questions this session can settle for free, both worth more than any tuning:
- **§16, the stop source.** Vision draws the far ribbon edge (`eS`). On the reference's frames, does the drawn stop sit on it or a couple of ticks under the signal candle? 0.19 points apart on their chart, but on your 7.85-ATR tape the two separate visibly.
- **§13 step 4, the frequency.** Count the gold bars in one RTH session. The design target is 8-12 fills; §2's single-sample cadence implies ~22. If you land near 22, that is a **different strategy** from the one the statistics were designed for — write down which one you chose to keep.
