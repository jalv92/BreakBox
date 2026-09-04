// TrendST.cs — trend pullback strategy on the BreakBoxVision cloud.
//
// Regime = the SAME BbCloud engine BreakBoxVision paints (EMA ribbon 300s/690s,
// trend line 1560s, latched regime). Green cloud -> longs only, red -> shorts
// only. Setup and signal live in TrendStCore.cs (pure, tested): a pullback into
// the ribbon with volume decaying from the tip, then a real engulfing bar
// (body covers the prior body, close above the prior high).
//
// Entry: market on the close of the signal bar (Calculate.OnBarClose).
// Exits: SetStopLoss / SetProfitTarget in TICKS (70 / 145 by default), set ONCE
// in State.Configure and never re-issued — so the working stop and target can
// be dragged in Chart Trader (to breakeven, wherever) and the strategy will not
// move them back. Flatten at FlattenHhmm, every day.
//
// ATM mode: tick "Use ATM strategy" and pick a template from the dropdown (it
// lists the templates saved on this machine). The template then OWNS the trade —
// its own stop, target and size; StopTicks / TargetTicks / Contracts are
// ignored. Realtime and Playback only: NT8 ignores AtmStrategyCreate on
// historical bars and in the Strategy Analyzer.
//
// CHART: NQ/MNQ 30-second bars, NT8 time zone US Eastern (every HHMM is ET).
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using BreakBoxCore;                           // per-file nt8c check reports CS0246 here: FALSE POSITIVE
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class TrendST : Strategy
    {
        #region Fields

        private WilderAtr _atr;
        private Ema _eF, _eS, _eT;
        private BbCloudConfig _cloudCfg;
        private BbCloudState _cloudSt;
        private BbCloud _cloud;
        private TrendStSetup _setup;
        private int _barSec = BbScale.FallbackSeconds;
        private int _startSecs, _endSecs, _flatSecs;

        private const string SigLong = "TrendST_L";
        private const string SigShort = "TrendST_S";

        // ATM: the template owns the trade once created and its fills reach none
        // of our handlers, so it is polled once per bar (pattern from PatternZone).
        private string _atmId = string.Empty, _atmOrderId = string.Empty;
        private bool _atmPending, _atmInPosition, _atmSeenOpen, _atmWaitPrinted;
        private bool _atmBlocked, _atmUnusableWarned;

        #endregion

        #region Lifecycle

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TrendST";
                Description = "Cloud-regime pullback with volume decay and an engulfing close. "
                    + "Fixed tick stop/target you can drag in Chart Trader, or an ATM template.";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                BarsRequiredToTrade = 0;         // warmup is the cloud's own gate

                Contracts = 1;
                StopTicks = 70;
                TargetTicks = 145;

                EntryStartHhmm = 935;
                EntryEndHhmm = 1550;
                FlattenHhmm = 1600;

                MinPullbackBars = 2;
                MaxPullbackBars = 10;
                VolumeDecayRatio = 0.8;

                // Cloud — BreakBoxVision defaults, seconds (converted in DataLoaded).
                RibbonFastSec = 300;
                RibbonSlowSec = 690;
                TrendLineSec = 1560;
                TrendSlopeSec = 300;
                TrendSlopeAtr = 0.15;
                RegimeMemorySec = 900;
                AtrPeriod = 14;

                UseAtmStrategy = false;
                AtmTemplateName = "";
            }
            else if (State == State.Configure)
            {
                // Once, static, never re-issued from OnBarUpdate: that is what
                // leaves the working orders free to be dragged in Chart Trader.
                SetStopLoss(CalculationMode.Ticks, StopTicks);
                SetProfitTarget(CalculationMode.Ticks, TargetTicks);
            }
            else if (State == State.DataLoaded)
            {
                _barSec = BarSeconds();
                int b = _barSec;
                _cloudCfg = new BbCloudConfig();
                _cloudCfg.TickSize = TickSize;
                _cloudCfg.RibbonFast = BbScale.Bars(RibbonFastSec, b, 2);
                _cloudCfg.RibbonSlow = BbScale.Bars(RibbonSlowSec, b, 2);
                _cloudCfg.TrendLine = BbScale.Bars(TrendLineSec, b, 2);
                _cloudCfg.TrendSlopeLookback = BbScale.Bars(TrendSlopeSec, b, 2);
                _cloudCfg.TrendSlopeAtr = TrendSlopeAtr;
                _cloudCfg.RegimeMemory = BbScale.Bars(RegimeMemorySec, b, 2);

                _atr = new WilderAtr(AtrPeriod);
                _eF = new Ema(_cloudCfg.RibbonFast);
                _eS = new Ema(_cloudCfg.RibbonSlow);
                _eT = new Ema(_cloudCfg.TrendLine);
                _cloudSt = new BbCloudState();
                _cloud = new BbCloud(_cloudCfg, _cloudSt);

                TrendStConfig sc = new TrendStConfig();
                sc.MinPullbackBars = MinPullbackBars;
                sc.MaxPullbackBars = MaxPullbackBars;
                sc.DecayRatio = VolumeDecayRatio;
                _setup = new TrendStSetup(sc);

                _startSecs = BbMath.HhmmToSecs(EntryStartHhmm);
                _endSecs = BbMath.HhmmToSecs(EntryEndHhmm);
                _flatSecs = BbMath.HhmmToSecs(FlattenHhmm);

                if (UseAtmStrategy)
                {
                    string tpl = (AtmTemplateName ?? string.Empty).Trim();
                    _atmBlocked = tpl.Length == 0 || !File.Exists(Path.Combine(AtmTemplateDir, tpl + ".xml"));
                    Print(_atmBlocked
                        ? Name + ": ATM mode is ON but template \"" + tpl + "\" is empty or not in " + AtmTemplateDir
                          + " — NO trading. Pick one from the dropdown or untick Use ATM strategy."
                        : Name + ": ATM mode ON, template \"" + tpl + "\" owns stop, target and size; StopTicks/TargetTicks/Contracts are ignored.");
                }

                Print(string.Format(CultureInfo.InvariantCulture,
                    "{0}: bar = {1}s -> ribbon {2}/{3}, trend {4} bars. Window {5}-{6}, flatten {7}.",
                    Name, _barSec, _cloudCfg.RibbonFast, _cloudCfg.RibbonSlow, _cloudCfg.TrendLine,
                    EntryStartHhmm, EntryEndHhmm, FlattenHhmm));
            }
        }

        // Seconds per bar for time series; anything else falls back to 30s and
        // says so (the model is calibrated on 30s time bars).
        private int BarSeconds()
        {
            int v = BarsPeriod.Value < 1 ? 1 : BarsPeriod.Value;
            switch (BarsPeriod.BarsPeriodType)
            {
                case BarsPeriodType.Second: return v;
                case BarsPeriodType.Minute: return v * 60;
                case BarsPeriodType.Day:    return v * 86400;
            }
            Print(Name + ": non-time series — assuming " + BbScale.FallbackSeconds + "s bars for every seconds dial.");
            return BbScale.FallbackSeconds;
        }

        #endregion

        #region Bar loop

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || CurrentBar < 1)
                return;

            BbBar bar;
            bar.Time = Time[0];
            bar.Open = Open[0];
            bar.High = High[0];
            bar.Low = Low[0];
            bar.Close = Close[0];
            bar.Volume = Volume[0];

            // Update first, then read — every gate sees post-update values,
            // exactly like BreakBoxVision.
            _atr.Update(bar);
            _eF.Update(bar.Close);
            _eS.Update(bar.Close);
            _eT.Update(bar.Close);

            int secs = Time[0].Hour * 3600 + Time[0].Minute * 60 + Time[0].Second;
            bool warm = _atr.IsWarm && _eF.IsWarm && _eS.IsWarm && _eT.IsWarm;

            // canTrade=false: only the latched regime is wanted from the cloud;
            // its own trigger/token machinery is not used here.
            _cloud.OnBar(bar, secs, _eF.Value, _eS.Value, _eT.Value, _atr.Value, warm, false, false);
            int regime = warm ? _cloudSt.RegimeLatched : 0;

            int signal = _setup.OnBar(bar, regime, _eF.Value);

            PollAtm();

            // Flatten window [FlattenHhmm, 24:00): close and take nothing new.
            if (secs >= _flatSecs)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("Flatten", SigLong);
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("Flatten", SigShort);
                CloseAtm("flatten time");
                return;
            }

            if (signal == 0)
                return;

            bool inWindow = BbMath.InWindow(secs, _startSecs, _endSecs);
            bool atmUnusable = UseAtmStrategy && (_atmBlocked || State != State.Realtime);
            if (atmUnusable && !_atmUnusableWarned)
            {
                _atmUnusableWarned = true;
                Print(Name + ": ATM mode takes no trades here — " + (_atmBlocked
                    ? "the template is unusable."
                    : "ATM strategies never run on historical data; on a live chart trading starts at realtime, in the Strategy Analyzer it means the whole run."));
            }
            bool flat = Position.MarketPosition == MarketPosition.Flat && _atmId.Length == 0;

            if (!inWindow || atmUnusable || !flat)
            {
                Print(string.Format(CultureInfo.InvariantCulture, "{0} {1:HH:mm:ss} signal {2} skipped — {3}",
                    Name, Time[0], signal > 0 ? "LONG" : "SHORT",
                    !inWindow ? "outside the entry window" : atmUnusable ? "ATM unusable" : "already in a trade"));
                return;
            }

            Print(string.Format(CultureInfo.InvariantCulture, "{0} {1:yyyy-MM-dd HH:mm:ss} ENTRY {2} @ {3} — {4}",
                Name, Time[0], signal > 0 ? "LONG" : "SHORT", Close[0].ToString("0.00", CultureInfo.InvariantCulture), _setup.Why));

            if (UseAtmStrategy)
                SubmitAtmEntry(signal);
            else if (signal > 0)
                EnterLong(Contracts, SigLong);
            else
                EnterShort(Contracts, SigShort);
        }

        #endregion

        #region ATM

        internal static string AtmTemplateDir
        {
            get { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "templates", "AtmStrategy"); }
        }

        // Every id and flag is written BEFORE AtmStrategyCreate: the callback runs
        // on the UI thread and can land before the call returns.
        private void SubmitAtmEntry(int dir)
        {
            string id = GetAtmStrategyUniqueId();
            string orderId = GetAtmStrategyUniqueId();
            _atmId = id;
            _atmOrderId = orderId;
            _atmPending = true;
            _atmInPosition = false;
            _atmSeenOpen = false;
            _atmWaitPrinted = false;

            AtmStrategyCreate(dir > 0 ? OrderAction.Buy : OrderAction.SellShort,
                OrderType.Market, 0, 0, TimeInForce.Day, orderId, AtmTemplateName.Trim(), id,
                (errorCode, callbackId) =>
                {
                    if (callbackId != id || errorCode == ErrorCode.NoError)
                        return;
                    Print(Name + ": ATM create FAILED (" + errorCode + ") — releasing the gate.");
                    ClearAtm();
                });
        }

        private void PollAtm()
        {
            if (_atmId.Length == 0 || State != State.Realtime)
                return;

            if (_atmPending)
            {
                string[] st = null;
                try { st = GetAtmStrategyEntryOrderStatus(_atmOrderId); } catch { }
                if (st != null && st.Length > 2)
                {
                    double filledQty;
                    double.TryParse(st[1], NumberStyles.Any, CultureInfo.InvariantCulture, out filledQty);
                    bool terminal = st[2] == "Cancelled" || st[2] == "Rejected";
                    if (st[2] == "Filled" || (terminal && filledQty > 0))
                    {
                        _atmPending = false;
                        _atmInPosition = true;
                        Print(Name + ": ATM entry filled at " + st[0] + " (" + st[2] + ").");
                        return;                  // the position is not reflected until next bar
                    }
                    if (terminal)
                    {
                        Print(Name + ": ATM entry " + st[2] + " unfilled — back to flat.");
                        ClearAtm();
                        return;
                    }
                }
            }

            if (!_atmInPosition)
                return;

            MarketPosition mp = MarketPosition.Flat;
            try { mp = GetAtmStrategyMarketPosition(_atmId); } catch { }
            if (mp != MarketPosition.Flat)
            {
                _atmSeenOpen = true;
                return;
            }

            // Flat is ambiguous (not reflected yet / closed / unknown id): trust it
            // only once the position was seen open or the ATM reports realized PnL.
            double pnl = 0;
            try { pnl = GetAtmStrategyRealizedProfitLoss(_atmId); } catch { }
            if (!_atmSeenOpen && (double.IsNaN(pnl) || pnl == 0))
            {
                if (!_atmWaitPrinted)
                {
                    _atmWaitPrinted = true;
                    Print(Name + ": waiting for the ATM position to reflect (id " + _atmId + ").");
                }
                return;
            }
            Print(string.Format(CultureInfo.InvariantCulture, "{0}: ATM trade closed, realized {1:0.##} USD.", Name, pnl));
            ClearAtm();
        }

        private void ClearAtm()
        {
            _atmId = string.Empty;
            _atmOrderId = string.Empty;
            _atmPending = false;
            _atmInPosition = false;
            _atmSeenOpen = false;
            _atmWaitPrinted = false;
        }

        // Flattens the position AND cancels the template's own stop/target.
        // Called every flatten bar until the poll sees flat.
        private void CloseAtm(string why)
        {
            if (_atmId.Length == 0 || State != State.Realtime)
                return;
            Print(Name + ": closing ATM (" + why + ").");
            try { AtmStrategyClose(_atmId); } catch { }
        }

        #endregion

        #region Properties

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Contracts", Description = "Position size. Ignored in ATM mode (the template sets it).", GroupName = "01. Trade", Order = 0)]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Stop loss (ticks)", Description = "Initial stop, ticks from entry. Placed once per trade; drag it in Chart Trader afterwards and the strategy will not move it back. Ignored in ATM mode.", GroupName = "01. Trade", Order = 1)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Profit target (ticks)", Description = "Initial target, ticks from entry. Same freedom as the stop. Ignored in ATM mode.", GroupName = "01. Trade", Order = 2)]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Entry window start (HHMM, ET)", GroupName = "02. Session", Order = 0)]
        public int EntryStartHhmm { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Entry window end (HHMM, ET)", GroupName = "02. Session", Order = 1)]
        public int EntryEndHhmm { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Flatten (HHMM, ET)", Description = "Close any open position at this time; nothing new after it.", GroupName = "02. Session", Order = 2)]
        public int FlattenHhmm { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Min pullback bars", Description = "Closed bars touching the ribbon before a signal can fire.", GroupName = "03. Setup", Order = 0)]
        public int MinPullbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Max pullback bars", Description = "A pullback longer than this is dropped.", GroupName = "03. Setup", Order = 1)]
        public int MaxPullbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.0)]
        [Display(Name = "Volume decay ratio", Description = "Volume of the last pullback bar must be below this x the volume of the tip (the bar before the pullback). 0.8 = at least 20% lower.", GroupName = "03. Setup", Order = 2)]
        public double VolumeDecayRatio { get; set; }

        [NinjaScriptProperty]
        [Range(30, int.MaxValue)]
        [Display(Name = "Ribbon fast (sec)", GroupName = "04. Cloud", Order = 0)]
        public int RibbonFastSec { get; set; }

        [NinjaScriptProperty]
        [Range(30, int.MaxValue)]
        [Display(Name = "Ribbon slow (sec)", GroupName = "04. Cloud", Order = 1)]
        public int RibbonSlowSec { get; set; }

        [NinjaScriptProperty]
        [Range(30, int.MaxValue)]
        [Display(Name = "Trend line (sec)", GroupName = "04. Cloud", Order = 2)]
        public int TrendLineSec { get; set; }

        [NinjaScriptProperty]
        [Range(30, int.MaxValue)]
        [Display(Name = "Trend slope lookback (sec)", GroupName = "04. Cloud", Order = 3)]
        public int TrendSlopeSec { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "Trend slope (ATR)", GroupName = "04. Cloud", Order = 4)]
        public double TrendSlopeAtr { get; set; }

        [NinjaScriptProperty]
        [Range(30, int.MaxValue)]
        [Display(Name = "Regime memory (sec)", GroupName = "04. Cloud", Order = 5)]
        public int RegimeMemorySec { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "ATR period", GroupName = "04. Cloud", Order = 6)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use ATM strategy", Description = "Route entries through an NT8 ATM template instead of the tick stop/target. The template then owns stop, target and size. Realtime/Playback only.", GroupName = "05. ATM", Order = 0)]
        public bool UseAtmStrategy { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(TrendStAtmTemplateConverter))]
        [Display(Name = "ATM template", Description = "One of the ATM templates saved on this machine (the same list Chart Trader shows). Required when ATM mode is on.", GroupName = "05. ATM", Order = 1)]
        public string AtmTemplateName { get; set; }

        #endregion
    }

    // Fills the "ATM template" dropdown from the templates saved on THIS machine.
    // StringConverter with non-exclusive values: a name can still be typed.
    public class TrendStAtmTemplateConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return false; }

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            var names = new List<string>();
            try
            {
                string dir = TrendST.AtmTemplateDir;
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.xml"))
                        names.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return new StandardValuesCollection(names);
        }
    }
}
