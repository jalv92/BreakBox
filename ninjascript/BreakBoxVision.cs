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

                // Vision's own state object. NOT the strategy's — nothing is
                // shared, nothing is read across processes, and the two are
                // expected to agree only because their dials were set to agree.
                // SlopeBuf is sized by BbCloud's own constructor (it checks
                // null-or-wrong-length itself, for the panel-toggle rebuild
                // case) — sizing it here too would just be a second place to
                // keep in sync with TrendSlopeLookback for no benefit.
                _cloudSt = new BbCloudState();
                _cloud = new BbCloud(_cloudCfg, _cloudSt);

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
