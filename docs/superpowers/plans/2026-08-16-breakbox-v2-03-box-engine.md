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
