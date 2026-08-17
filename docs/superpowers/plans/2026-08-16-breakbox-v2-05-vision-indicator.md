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
