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
