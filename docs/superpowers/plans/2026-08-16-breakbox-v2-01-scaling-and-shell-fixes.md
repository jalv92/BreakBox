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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: `ALL PASS (N checks)`

- [ ] **Step 5: Commit**

```bash
cd "<repo>" && \
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: `ALL PASS (N checks)`

- [ ] **Step 5: Commit**

```bash
cd "<repo>" && \
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
cd "<repo>" && \
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

Run: `cd "<repo>" && grep -rn 'TriggerLifeBars' ninjascript/ tests/ ; bash scripts/check.sh`
Expected: the grep prints nothing (exit 1 from grep is expected), then `ALL PASS` and `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "<repo>" && \
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: `ALL PASS (N checks)`

- [ ] **Step 5: Commit**

```bash
cd "<repo>" && \
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "error|FAIL|ALL PASS"`
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: `ALL PASS (<n> checks)`

- [ ] **Step 5: Commit**

```bash
cd "<repo>" && git add ninjascript/BreakBoxTypes.cs tests/VisionTests.cs tests/Program.cs && git commit -m "feat(core): BbMath.CloseInRange — the shared signal-candle ratio, NaN on a flat bar

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
cd "<repo>" && \
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS" && bash scripts/check.sh`
Expected: `ALL PASS (N checks)` and `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "<repo>" && \
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
Expected: `ALL PASS (N checks)`

- [ ] **Step 5: Commit**

```bash
cd "<repo>" && \
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
cd "<repo>" && \
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

Run: `cd "<repo>" && grep -cE 'OnEntryRejected\(' ninjascript/BreakBoxStrategy.cs && bash scripts/check.sh`
Expected: `4` (the three call sites plus the definition), then `ALL PASS` and `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "<repo>" && \
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS" && grep -c 'degenerate stop' ninjascript/BreakBoxStrategy.cs`
Expected: `ALL PASS (N checks)`, then `0` from the grep

- [ ] **Step 5: Commit**

```bash
cd "<repo>" && \
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS" && grep -n 'DailyLossLimit = ' ninjascript/BreakBoxStrategy.cs`
Expected: `ALL PASS (N checks)` and `DailyLossLimit = 450;`

- [ ] **Step 5: Commit**

```bash
cd "<repo>" && \
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS"`
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

Run: `cd "<repo>" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS|error CS" && bash scripts/check.sh`
Expected: `ALL PASS (N checks)` and `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "<repo>" && \
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
