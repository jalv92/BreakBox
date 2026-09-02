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
        private BbCloudState _cloudSt;
        private BbCloud _cloud;
        private BbAction _lastAction;
        private int _barSec = BbScale.FallbackSeconds;

        // The ETH open, ET. Not a parameter: it anchors the trading day for the
        // box engine and nothing about this picture is worth a dial for it.
        private const int SessionOpenHhmm = 1800;

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

        // Gold = every gate passed. Dim gold = the BAR looked right and the
        // CONTEXT did not. The dim bars are the free diagnostic: a session full
        // of them means the candle gates are fine and the token/regime/leg
        // gates are what is starving the strategy (spec §5.3).
        private static readonly Brush GoldBrush = Brushes.Gold;
        private static readonly Brush DimGoldBrush = Brushes.DarkGoldenrod;

        private BbConfig _boxCfg;
        private BbEngineState _boxSt;
        private BbEngine _boxEngine;
        private int _lastDrawnBoxId = -1;

        // White, like the reference. The cyan 4H rectangle in those frames is
        // distant context and is NOT this object (spec §1).
        private static readonly Brush BoxBrush = Brushes.White;

        // Markers come from the history FILE (spec §10), never from execution
        // events. No coupling to a running strategy: this works on a chart with
        // nothing attached, and on last week's session.
        private readonly List<BbTradeRecord> _history = new List<BbTradeRecord>();
        private int _histIdx;
        private DateTime _firstBarTime = DateTime.MinValue;

        private static readonly Brush EntryBrush = Brushes.DodgerBlue;    // blue up-arrow, per §2.2
        private static readonly Brush ExitBrush = Brushes.Magenta;        // magenta down-arrow

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

                EnableCloud = true;
                EnableBox = false;

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
            }
            else if (State == State.DataLoaded)
            {
                _barSec = BarSeconds();
                BuildConfigs();

                // Engine off = nothing on the chart from it. The ribbon plots
                // are cloud output, so they go transparent with it.
                if (!EnableCloud)
                    for (int i = 0; i < 3; i++)
                        Plots[i].Brush = Brushes.Transparent;

                _atr = new WilderAtr(AtrPeriod);
                _eF = new Ema(_cloudCfg.RibbonFast);
                _eS = new Ema(_cloudCfg.RibbonSlow);
                _eT = new Ema(_cloudCfg.TrendLine);

                // Vision's own state object. NOT the strategy's — nothing is
                // shared, nothing is read across processes, and the two are
                // expected to agree only because their dials were set to agree.
                // SlopeBuf is sized by BbCloud's own constructor (it checks
                // null-or-wrong-length itself, for the panel-toggle rebuild
                // case) — sizing it here too would just be a second place to
                // keep in sync with TrendSlopeLookback for no benefit.
                _cloudSt = new BbCloudState();
                _cloud = new BbCloud(_cloudCfg, _cloudSt);

                _boxSt = new BbEngineState();
                _boxEngine = new BbEngine(_boxCfg, _boxSt);

                LoadHistory();

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
        }

        #endregion

        #region Bar loop

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1)
                return;

            if (_firstBarTime == DateTime.MinValue)
                _firstBarTime = Time[0];

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

            int secs = Time[0].Hour * 3600 + Time[0].Minute * 60 + Time[0].Second;

            // canTrade = true, positioned = false, ALWAYS. Vision has no
            // lockout, no window and no position, and hiding triggers behind a
            // simulated governor is how a calibration view starts explaining
            // away the very bars you are trying to count.
            // Either engine can be switched off from the dialog: it neither
            // runs nor paints. BOTH run by default-config arbitration in the
            // strategy; here the switch is purely the operator's choice of
            // what to look at.
            if (EnableCloud)
            {
                _lastAction = _cloud.OnBar(bar, secs, _eF.Value, _eS.Value, _eT.Value,
                                           _atr.Value, _atr.IsWarm, true, false);

                // Assume every trigger filled. Vision has no order layer, so the
                // alternative is a token that stays minted forever and a chart that
                // paints a gold candle on every subsequent bar of the same pullback.
                if (_lastAction.Fire)
                    _cloud.OnEntryFilled();

                PaintCloud();
                PaintSignalBar(bar);
            }

            // No §4.1 arbitration here: the box never hides because the cloud
            // armed first. Only the operator's switch hides it — a calibration
            // view that hid the box on its own would hide exactly the trades
            // you are trying to account for.
            if (EnableBox)
            {
                DateTime sessionDate = SessionDateOf(Time[0], secs);
                BbAction boxAction = _boxEngine.OnBar(bar, secs, sessionDate, _atr.Value, _atr.IsWarm, true, false);
                if (boxAction.Fire)
                    _boxEngine.OnEntryFilled();

                PaintBox();
            }

            DrawMarkers(Time[0]);
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

        #endregion

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

        // One rectangle per SEALED box, drawn once when its Id changes. A box's
        // edges never move after the seal (spec §6.1), so redrawing it every
        // bar would buy nothing and cost 780 draw objects a session.
        //
        // Right edge = `Time[1]`, NOT `Time[0]` / `SealedAt`. WindowRange()
        // (BreakBoxCore.cs:565) reads the ring BEFORE Push(bar) runs, so the
        // measured window is `Time[1] .. Time[BoxLookback]` — it deliberately
        // EXCLUDES the sealing bar itself (the one-bar-lookahead guard pinned
        // by tests/BoxTests.cs's FormationExcludesTheCurrentBar). Drawing to
        // `Time[0]` put the sealing bar's own candle inside a box its high/low
        // were never measured against: if that candle's wick pokes past
        // `box.High`/`box.Low` (Form() only gates its CLOSE, not its wick —
        // BreakBoxCore.cs:657), the operator sees a candle sticking outside
        // the box that supposedly contains it. Left edge stays `Time[BoxLookback]`
        // — the box's own High/Low ARE the range of the last BoxLookback CLOSED
        // bars (the Lifecycle window), so walking back that many bars from the
        // sealing bar covers exactly the bars the range was measured over, no
        // more. Indexing `Time[]` (not `SealedAt` minus N seconds) means the
        // left edge survives session gaps and weekends the way a literal time
        // subtraction would not. `Math.Min` guards a box sealed with fewer than
        // BoxLookback bars of chart history (should not happen in practice —
        // the window can't fill without that many bars — but an
        // IndexOutOfRange here would take the whole indicator down for a
        // drawing bug); `Time[1]` needs no matching guard because OnBarUpdate's
        // `CurrentBar < 1` return (line 276) already guarantees `CurrentBar >= 1`
        // — i.e. at least two bars — everywhere PaintBox() runs.
        private void PaintBox()
        {
            BbBox box = _boxEngine.Box;
            if (box == null || box.Id == _lastDrawnBoxId)
                return;
            _lastDrawnBoxId = box.Id;

            int barsBack = Math.Min(_boxCfg.BoxLookback, CurrentBar);
            DateTime left = Time[barsBack];

            DrawTag(Draw.Rectangle(this, "bbv_box_" + box.Id, false,
                                   left, box.Low, Time[1], box.High,
                                   BoxBrush, BoxBrush, 6));
        }

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

            // BbHistory.FileName keys the instrument segment off Instrument.FullName
            // (e.g. "MNQ 12-25", spaces -> underscores) — NOT MasterInstrument.Name
            // ("MNQ", no contract month). Globbing on MasterInstrument.Name matches
            // nothing the strategy ever writes, so this mirrors FileName's own
            // cleaning instead of re-deriving a different, wrong prefix.
            string insName = Instrument != null ? Instrument.FullName : "";
            string pattern = "history-" + insName.Replace(' ', '_') + "-*.jsonl";
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
                catch (UnauthorizedAccessException ex)
                {
                    // UnauthorizedAccessException does NOT derive from
                    // IOException in .NET — a permissions-denied file (ACL,
                    // read-only, another process holding it exclusively)
                    // would otherwise escape uncaught during State.DataLoaded
                    // and take the whole indicator down. Same degrade as
                    // above: a history file Vision cannot read costs markers,
                    // never the indicator, because this has to work on a
                    // chart with no strategy ever attached.
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

        // Same tag-ring discipline as the strategy (BreakBoxStrategy.cs's own
        // DrawTag) and for the same reason. The last-tag check keeps a region
        // that is redrawn on every bar of its segment from filling the ring
        // with one repeated name.
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

        #region Parameters

        [NinjaScriptProperty]
        [Display(Name = "Cloud", Description = "Run and paint the cloud engine (ribbon, region, signal candles)", Order = 1, GroupName = "00. Engines")]
        public bool EnableCloud { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Box", Description = "Run and paint the box engine (white rectangles)", Order = 2, GroupName = "00. Engines")]
        public bool EnableBox { get; set; }

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

        #endregion
    }
}
