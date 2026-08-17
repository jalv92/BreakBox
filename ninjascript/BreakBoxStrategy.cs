// BreakBoxStrategy — a box-breakout engine inside a session-aware execution
// shell. Build a box from a completed period, trade its break or the retrace
// back to its edge, protect it with a structural stop and up to three
// R-multiple take-profit tiers.
//
// Every decision lives in BreakBoxCore.cs / BreakBoxExits.cs (same
// Custom/Strategies folder, pure C#, unit-tested in tests/). This file is
// plumbing: series, indicators, orders, drawing, the session window and the
// daily governor. The on-chart panel and HUD live in BreakBoxPanel.cs, a
// partial of this same class.
//
// Spec and provenance: docs/research/01-target-analysis.md. The bracket geometry
// (R-multiple tiers, structural stop, the 4/2/1 split) is reverse-engineered
// from black-box observation of a competitor product — clean room, from
// screenshots, never from their binary. See §2 of that document.
//
// CHART REQUIREMENTS:
//   * Primary series = 1 Minute. Every ATR-scaled gate and every bar budget in
//     the core counts 1m bars.
//   * The instrument's FULL ETH session template, NOT an RTH one. The box is
//     built from overnight periods; on an RTH template the 18:00-and-later
//     slots never form.
//   * NT8's global time zone = US Eastern. Every HHMM parameter is ET wall
//     clock.
//
// NO SECOND DATA SERIES. The HTF box is folded from the primary 1m series
// inside BbEngine (see its header). One series, no AddDataSeries fold indices,
// and the box is reproducible in the test runner.
//
// ATR/EMA: BreakBoxCore.WilderAtr and BreakBoxCore.Ema, hand-rolled and fed from
// OnBarUpdate. nt8c cannot resolve NT8's ATR()/EMA() wrappers from the pure
// files, and the recursions here are the ones the unit tests pin.
//
// ORDERS: entry is a stop-market beyond the break bar (Break) or a limit at the
// box edge (Retrace). Exits are ONE stop covering the open quantity plus one
// limit per live tier, each with its own signal name. There is deliberately NO
// SetStopLoss / SetProfitTarget / SetTrailStop anywhere in this file: the
// managed approach ignores Exit* while a Set* is active, and the Set* orders
// are the ones the engine re-submits, which silently reverts a bracket the user
// dragged by hand.
//
// THE ORDER-EVENT RACE RULES THIS FILE. NT8 — Playback especially — can deliver
// OnOrderUpdate/OnExecutionUpdate synchronously, in-stack, BEFORE the Enter*/
// Exit* call that caused them returns. So every in-flight flag is written
// BEFORE its submit, and every clear in the handlers is gated on the signal
// NAME that set it. See memory [[nt8-order-event-race]].
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;   // REQUIRED for Draw.* — nt8c won't miss it, F5 will
using BreakBoxCore;                           // per-file nt8c check reports CS0246 here: FALSE POSITIVE
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class BreakBoxStrategy : Strategy
    {
        #region Constants and identity

        private const string SigLong = "BB_Long";
        private const string SigShort = "BB_Short";
        private const string SigStop = "BB_Stop";
        private const string SigTp1 = "BB_TP1";
        private const string SigTp2 = "BB_TP2";
        private const string SigTp3 = "BB_TP3";
        private const string SigFlatten = "BB_Flatten";

        private static readonly string[] TierSig = { SigTp1, SigTp2, SigTp3 };

        // Order-layer mechanics, NOT dials. None of these has a counterpart on
        // the panel, and none of them changes what the strategy trades — only
        // how patiently it talks to the platform.
        private const int ExitBufferTicks = 4;              // clearance from the inside market
        private const double ExitChangeWatchdogSec = 5.0;   // wall clock, never tape time
        private const double BracketCancelGraceSec = 1.0;   // before calling a cancel "by hand"
        private const int MaxDrawTags = 4000;

        #endregion

        #region Fields

        private BbConfig _cfg;
        private BbExitConfig _exitCfg;
        private BbEngineState _engState;
        private BbEngine _engine;
        private BbBracket _bracket;

        private WilderAtr _atr;
        private Ema _ma, _e50;
        private SwingDetector _swings;
        private double _lastSwingHigh = double.NaN, _lastSwingLow = double.NaN;

        // Trade / order state. Every one of these is written BEFORE the submit
        // that makes it true (the order-event race).
        private bool _inTrade, _entryPending, _flattenPending;
        private int _dir, _qty;
        private string _entrySig = "";
        private Order _entryOrder, _stopOrder;
        private readonly Order[] _tierOrders = new Order[BbExitConfig.MAX_TIERS];
        private BbAction _pendingAction;
        // §4.1. Which engine armed the trigger this order came from. Expiry,
        // disarm and rejection are forwarded ONLY to it, so a cloud refusal can
        // never cancel the box's trigger once both engines are live.
        private BbEntryEngine _owningEngine;
        // A hand-clicked entry owns no token: nothing armed it, so nothing may
        // be handed back to an engine when it is cancelled.
        private bool _entryFromEngine;
        private DateTime _entryTime;
        private double _entryFillPx;
        private string _exitReason = "";
        private int _entryBarsWaiting;

        // §8. The whole parameter surface is seconds; this pair is the only
        // place that knows how many of them a bar is worth. Cached at
        // DataLoaded because the estimate walks the loaded history and
        // BuildConfigs runs again on every panel click.
        private int _barSec = BbScale.FallbackSeconds;
        private string _barSecLabel = "";

        private volatile bool _stopChangePending;
        private DateTime _stopChangeSentAt = DateTime.MinValue;
        private double _lastStopSent = double.NaN;
        private DateTime _stopCancelAt = DateTime.MinValue;

        // Session / governor
        private DateTime _sessionDate = DateTime.MinValue;
        private double _dayStartCum = double.NaN;
        private bool _lockout;
        private string _lockoutWhy = "";
        private int _tradesToday, _winsToday, _lossesToday;
        private double _dayPnl;
        private readonly List<double> _tradePnls = new List<double>();

        // Panel-driven overrides. The panel writes them, OnBarUpdate reads them.
        // They start as the parameter values and diverge only when a human
        // clicks something.
        private bool _uiAutoTrade = true;
        private bool _uiBreakOn, _uiRetraceOn, _uiLongOn, _uiShortOn;
        private double _uiRiskMult = 1.0;
        private BbStopSource _uiStopSource;

        // Drawing
        private readonly List<string> _drawTags = new List<string>();
        private int _tagSeq;
        private int _lastDrawnBoxId = -1;

        #endregion

        #region Lifecycle

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "BreakBoxStrategy";
                Description = "Box breakout / retrace with a structural stop and up to three R-multiple "
                    + "take-profit tiers. Requires a 1-Minute primary series on a FULL ETH session template.";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                BarsRequiredToTrade = 0;        // warmup is enforced against the ATR, not a bar count
                // NOT StopCancelClose. This strategy re-prices its stop while a
                // runner is trailing, so a modify losing a race against a fast
                // market is an expected event — and StopCancelClose would
                // flatten the position and kill the strategy for one of them.
                RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;

                // ---- Sizing
                BaseQuantity = 3;
                RiskMultiplier = 1.0;

                // ---- Box
                BoxSourceParam = BbBoxSource.PriorPeriod;
                HtfMinutes = 240;
                SessionOpenHhmm = 1800;
                IbStartHhmm = 930;
                IbMinutes = 60;
                MinBoxRangeAtr = 0.5;
                MaxBoxRangeAtr = 6.0;

                // ---- Engines
                EnableBreak = true;
                EnableRetrace = false;
                AllowLong = true;
                AllowShort = true;
                BreakBufferTicks = 4;
                RequireCloseOutside = true;
                TriggerLifeSec = 120;
                ExtensionAtr = 1.0;
                RetraceMaxBars = 30;
                RetraceOffsetTicks = 0;

                // ---- Stop
                StopSourceParam = BbStopSource.Candle;
                StopBufferTicks = 2;
                ManualStopTicks = 40;
                StopMinAtr = 0.5;
                StopMaxAtr = 3.0;
                SwingStrength = 3;
                MaPeriod = 20;
                E50Period = 50;

                // ---- Targets. PROVISIONAL: these are the multiples measured
                // off one of the two observed configs. The whole finding is that
                // they are optimised per session, so treat them as a starting
                // point, not as the model.
                TierCount = 3;
                Tp1R = 0.5;
                Tp2R = 1.0;
                Tp3R = 1.5;
                Tp1Pct = 50;
                Tp2Pct = 30;
                BreakevenOnTp1 = true;
                BreakevenOffsetTicks = 1;
                TrailAfterTp2 = true;
                TrailAtrMult = 1.5;

                // ---- Session and governor
                EntryWindowStartHhmm = 1800;
                EntryWindowEndHhmm = 1600;
                FlattenHhmm = 1655;
                MaxTradesPerBox = 1;
                MaxTradesPerDay = 5;
                DailyLossLimit = 0;             // 0 = off, currency
                DailyProfitTarget = 0;          // 0 = off, currency
                AtrPeriod = 14;

                // ---- Visuals
                ShowBox = true;
                ShowLevels = true;
                ShowPanel = true;
                ShowHud = true;
            }
            else if (State == State.Configure)
            {
                _bracket = new BbBracket();
                _engState = new BbEngineState();
            }
            else if (State == State.DataLoaded)
            {
                _barSec = BarSeconds();
                Print("BreakBox: bar ~ " + _barSec + "s (" + _barSecLabel + ")");
                BuildConfigs();
                _engine = new BbEngine(_cfg, _engState);
                _atr = new WilderAtr(AtrPeriod);
                _ma = new Ema(MaPeriod);
                _e50 = new Ema(E50Period);
                _swings = new SwingDetector(SwingStrength);

                _uiBreakOn = EnableBreak;
                _uiRetraceOn = EnableRetrace;
                _uiLongOn = AllowLong;
                _uiShortOn = AllowShort;
                _uiRiskMult = RiskMultiplier;
                _uiStopSource = StopSourceParam;

                if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
                    Print("BreakBox WARNING: primary series is not 1-Minute. Every ATR gate and bar budget "
                          + "in the core counts 1m bars; this is a different experiment.");
            }
            else if (State == State.Realtime)
            {
                BuildPanel();
            }
            else if (State == State.Terminated)
            {
                DisposePanel();
            }
        }

        // Rebuilt from the parameters at DataLoaded, and again whenever the
        // panel changes something. The pure engines own no NT8 state, so
        // swapping their config is just an assignment.
        private void BuildConfigs()
        {
            _cfg = new BbConfig();
            _cfg.TickSize = TickSize;
            _cfg.BoxSource = BoxSourceParam;
            _cfg.HtfMinutes = HtfMinutes;
            _cfg.SessionOpenHhmm = SessionOpenHhmm;
            _cfg.IbStartHhmm = IbStartHhmm;
            _cfg.IbMinutes = IbMinutes;
            _cfg.SessionCloseHhmm = FlattenHhmm;
            _cfg.MinBoxRangeAtr = MinBoxRangeAtr;
            _cfg.MaxBoxRangeAtr = MaxBoxRangeAtr;
            _cfg.EnableBreak = _uiBreakOn;
            _cfg.EnableRetrace = _uiRetraceOn;
            _cfg.AllowLong = _uiLongOn;
            _cfg.AllowShort = _uiShortOn;
            _cfg.BreakBufferTicks = BreakBufferTicks;
            _cfg.RequireCloseOutside = RequireCloseOutside;
            // §8. The conversion lives HERE, not at DataLoaded: a panel toggle
            // rebuilds this config too, and a rebuild that skipped the
            // conversion would hand the engine raw seconds as a bar count.
            _cfg.TriggerLife = BbScale.Bars(TriggerLifeSec, _barSec, 1);
            _cfg.ExtensionAtr = ExtensionAtr;
            _cfg.RetraceMaxBars = RetraceMaxBars;
            _cfg.RetraceOffsetTicks = RetraceOffsetTicks;
            _cfg.MaxTradesPerBox = MaxTradesPerBox;
            _cfg.MaxTradesPerDay = MaxTradesPerDay;
            _cfg.EntryWindowStartHhmm = EntryWindowStartHhmm;
            _cfg.EntryWindowEndHhmm = EntryWindowEndHhmm;
            _cfg.AtrPeriod = AtrPeriod;

            _exitCfg = new BbExitConfig();
            _exitCfg.TickSize = TickSize;
            _exitCfg.StopSource = _uiStopSource;
            _exitCfg.StopBufferTicks = StopBufferTicks;
            _exitCfg.ManualStopTicks = ManualStopTicks;
            _exitCfg.StopMinAtr = StopMinAtr;
            _exitCfg.StopMaxAtr = StopMaxAtr;
            _exitCfg.TierCount = TierCount;
            _exitCfg.Tp1R = Tp1R;
            _exitCfg.Tp2R = Tp2R;
            _exitCfg.Tp3R = Tp3R;
            _exitCfg.Tp1Pct = Tp1Pct;
            _exitCfg.Tp2Pct = Tp2Pct;
            _exitCfg.BreakevenOnTp1 = BreakevenOnTp1;
            _exitCfg.BreakevenOffsetTicks = BreakevenOffsetTicks;
            _exitCfg.TrailAfterTp2 = TrailAfterTp2;
            _exitCfg.TrailAtrMult = TrailAtrMult;

            // The engine holds a reference to _cfg, so a rebuild has to be
            // handed over rather than left dangling on the old object.
            if (_engine != null)
                _engine = new BbEngine(_cfg, _engState);
        }

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

        #endregion

        #region Bar loop

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1)
                return;

            BbBar bar = ToBar(0);
            _atr.Update(bar);
            _ma.Update(bar.Close);
            _e50.Update(bar.Close);

            var found = _swings.Update(bar, CurrentBar);
            for (int i = 0; i < found.Count; i++)
            {
                if (found[i].IsHigh) _lastSwingHigh = found[i].Price;
                else _lastSwingLow = found[i].Price;
            }

            // Seconds-of-day off the DateTime, not off ToTime(): ToTime returns
            // HHMMSS packed into an int, and dividing it is a classic way to get
            // a number that looks like seconds and is not.
            int secs = Time[0].Hour * 3600 + Time[0].Minute * 60 + Time[0].Second;
            DateTime sessionDate = SessionDateOf(Time[0], secs);

            RollSession(sessionDate);

            // Manage the open trade FIRST. A bracket that needs to tighten must
            // not wait behind a signal evaluation that cannot fire anyway.
            if (_inTrade)
            {
                // Watchdog. A modify that NT8 swallowed leaves _lastStopSent
                // claiming the new price is live while the exchange still holds
                // the old one, and nothing would ever try again: the next
                // candidate compares equal and returns early. Forgetting what we
                // think we sent is what makes the retry possible.
                if (_stopChangePending
                    && (DateTime.Now - _stopChangeSentAt).TotalSeconds > ExitChangeWatchdogSec)
                {
                    Print("BreakBox: stop modify unacknowledged after "
                          + ExitChangeWatchdogSec + "s — resubmitting");
                    _stopChangePending = false;
                    _lastStopSent = double.NaN;
                    SubmitStop("watchdog");
                }

                var d = BbExits.OnBarClose(_exitCfg, _bracket, bar, _atr.Value);
                if (d.StopMoved)
                    SubmitStop("bar:" + d.Why);
                DrawLevels();
            }

            if (TimeToFlatten(secs))
            {
                FlattenAll("session_window");
                UpdateHud();
                return;
            }

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

            UpdateHud();
            UpdatePanelStatus();
        }

        private BbBar ToBar(int ago)
        {
            BbBar b;
            b.Time = Time[ago];
            b.Open = Open[ago];
            b.High = High[ago];
            b.Low = Low[ago];
            b.Close = Close[ago];
            b.Volume = Volume[ago];
            return b;
        }

        // Which trading day does this bar belong to? The ETH session opens at
        // 18:00 ET, so an 18:30 bar belongs to the NEXT calendar day's session.
        // Getting this wrong silently halves the daily trade budget on some days
        // and doubles it on others.
        private DateTime SessionDateOf(DateTime t, int secs)
        {
            int open = BbMath.HhmmToSecs(SessionOpenHhmm);
            return secs >= open ? t.Date.AddDays(1) : t.Date;
        }

        private void RollSession(DateTime sessionDate)
        {
            if (_sessionDate == sessionDate)
                return;
            _sessionDate = sessionDate;
            _dayStartCum = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            _tradesToday = 0;
            _winsToday = 0;
            _lossesToday = 0;
            _dayPnl = 0.0;
            _tradePnls.Clear();
            // The lockout is a DAILY limit, so a new session clears it. A
            // hand-pulled Lock Out is not: the panel sets a separate latch that
            // only the panel clears.
            if (_lockoutWhy != "manual")
            {
                _lockout = false;
                _lockoutWhy = "";
            }
        }

        private bool TimeToFlatten(int secs)
        {
            if (!_inTrade && !_entryPending)
                return false;
            int flat = BbMath.HhmmToSecs(FlattenHhmm);
            // Only fires on the bar that CROSSES the boundary, which on a 1m
            // series is the minute the flatten time falls in.
            return secs >= flat && secs < flat + 60;
        }

        #endregion

        #region Entry

        private void SubmitEntry(BbAction a)
        {
            int qty = SizedQty();
            if (qty < 1)
            {
                // B4. The engine armed this trigger; the shell is throwing it
                // away. Say so, or the edge stays spent on a trade that was
                // never attempted.
                OnEntryRejected(a.Engine, "qty<1");
                return;
            }

            // Refuse the trade at SUBMIT time if the stop it would get is
            // unusable — discovering it at the fill means holding a position
            // while deciding what to do about it.
            var inp = StopInputs(a);
            string why;
            double probeStop = BbExits.SeedStop(_exitCfg, a.Dir, a.TriggerPx, _atr.Value, _atr.IsWarm, inp, out why);
            if (Math.Abs(a.TriggerPx - probeStop) < TickSize)
            {
                Print("BreakBox: entry refused, degenerate stop (" + why + ")");
                return;
            }

            string sig = a.Dir > 0 ? SigLong : SigShort;

            // Written BEFORE the submit: NT8 can deliver the fill in-stack.
            _entryPending = true;
            _owningEngine = a.Engine;
            _entryFromEngine = true;
            _dir = a.Dir;
            _qty = qty;
            _entrySig = sig;
            _pendingAction = a;
            _entryBarsWaiting = 0;

            if (a.IsLimit)
            {
                if (a.Dir > 0) EnterLongLimit(0, true, qty, a.TriggerPx, sig);
                else EnterShortLimit(0, true, qty, a.TriggerPx, sig);
            }
            else
            {
                if (a.Dir > 0) EnterLongStopMarket(0, true, qty, a.TriggerPx, sig);
                else EnterShortStopMarket(0, true, qty, a.TriggerPx, sig);
            }

            if (ShowLevels)
                DrawTag(Draw.Text(this, Tag("sig"), (a.Dir > 0 ? "BUY " : "SELL ") + a.Why, 0,
                                  a.Dir > 0 ? Low[0] - 4 * TickSize : High[0] + 4 * TickSize,
                                  a.Dir > 0 ? Brushes.LimeGreen : Brushes.OrangeRed));
        }

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

        private int SizedQty()
        {
            double q = BaseQuantity * _uiRiskMult;
            int qty = (int)Math.Floor(q + 0.5);
            return qty < 1 ? 1 : qty;
        }

        private BbStopInputs StopInputs(BbAction a)
        {
            BbStopInputs inp;
            inp.SignalBarHigh = a.SignalBarHigh;
            inp.SignalBarLow = a.SignalBarLow;
            inp.LastSwingHigh = _lastSwingHigh;
            inp.LastSwingLow = _lastSwingLow;
            inp.MaValue = _ma.IsWarm ? _ma.Value : double.NaN;
            inp.EmaValue = _e50.IsWarm ? _e50.Value : double.NaN;
            return inp;
        }

        #endregion

        #region Exits

        // Prices and submits the whole bracket. Called once, from the entry fill.
        private void OpenBracket(double fillPx, int qty)
        {
            var inp = StopInputs(_pendingAction);
            BbExits.OnEntryFill(_exitCfg, _bracket, _dir, fillPx, qty, _atr.Value, _atr.IsWarm, inp);

            Print(string.Format(CultureInfo.InvariantCulture,
                "BreakBox {0} {1} @ {2} stop {3} ({4}) R={5:0.##} tiers={6}",
                _pendingAction.Why, _dir > 0 ? "LONG" : "SHORT", fillPx, _bracket.StopPx,
                _bracket.StopWhy, _bracket.R, _bracket.Tiers));

            SubmitStop("init");
            for (int i = 0; i < _bracket.Tiers; i++)
                SubmitTier(i);
            DrawLevels();
        }

        private void SubmitStop(string why)
        {
            if (!_inTrade || _bracket.StopCancelled)
                return;

            int qty = _bracket.QtyOpen;
            if (qty < 1)
                return;

            double px = _bracket.StopPx;

            // Never submit a stop already through the market: it becomes a
            // market order at whatever the next print is, which turns "protect
            // the position" into "exit now, worse".
            double inside = _dir > 0 ? GetCurrentBid() : GetCurrentAsk();
            if (!double.IsNaN(inside) && inside > 0)
            {
                double limit = inside - _dir * ExitBufferTicks * TickSize;
                if ((px - limit) * _dir > 0.0)
                    px = RoundTick(limit);
            }

            if (!double.IsNaN(_lastStopSent) && Math.Abs(px - _lastStopSent) < TickSize / 2.0)
                return;                                 // nothing changed

            _stopChangePending = true;
            _stopChangeSentAt = DateTime.Now;
            _lastStopSent = px;

            if (_dir > 0) ExitLongStopMarket(0, true, qty, px, SigStop, _entrySig);
            else ExitShortStopMarket(0, true, qty, px, SigStop, _entrySig);
        }

        private void SubmitTier(int i)
        {
            if (!_inTrade || i < 0 || i >= _bracket.Tiers || _bracket.TierFilled[i])
                return;
            int qty = _bracket.TargetQty[i];
            if (qty < 1)
                return;
            double px = _bracket.TargetPx[i];
            if (px <= 0.0)
                return;

            if (_dir > 0) ExitLongLimit(0, true, qty, px, TierSig[i], _entrySig);
            else ExitShortLimit(0, true, qty, px, TierSig[i], _entrySig);
        }

        private void FlattenAll(string why)
        {
            if (_flattenPending)
                return;
            _flattenPending = true;
            _exitReason = why;

            CancelWorkingEntry(why);
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (_dir > 0) ExitLong(SigFlatten, _entrySig);
                else ExitShort(SigFlatten, _entrySig);
            }
            else
            {
                _flattenPending = false;
            }
        }

        private void WentFlat(double exitPx)
        {
            double pnl = (exitPx - _bracket.EntryPx) * _bracket.Dir
                         * _bracket.QtyTotal * Instrument.MasterInstrument.PointValue;

            _inTrade = false;
            _flattenPending = false;
            _stopChangePending = false;
            _lastStopSent = double.NaN;
            _entryOrder = null;
            _stopOrder = null;
            for (int i = 0; i < BbExitConfig.MAX_TIERS; i++)
                _tierOrders[i] = null;

            _bracket.Dir = 0;
            _dir = 0;

            CheckDailyLimits();
        }

        #endregion

        #region Order events

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
                                                  int quantity, MarketPosition marketPosition,
                                                  string orderId, DateTime time)
        {
            if (execution.Order == null)
                return;
            string sig = execution.Order.Name;

            // Entry fill. Gated on the signal NAME, not on a bool: by the time
            // this arrives, another submit may already have flipped the flag.
            if ((sig == SigLong || sig == SigShort) && execution.Order.OrderState == OrderState.Filled)
            {
                _entryPending = false;
                _entryFromEngine = false;
                _entryBarsWaiting = 0;
                _inTrade = true;
                _entryFillPx = price;
                _entryTime = time;
                _engine.OnEntryFilled();
                _tradesToday++;
                OpenBracket(price, execution.Order.Filled);
                return;
            }

            // A tier filled.
            for (int i = 0; i < BbExitConfig.MAX_TIERS; i++)
            {
                if (sig != TierSig[i])
                    continue;
                var d = BbExits.OnTierFill(_exitCfg, _bracket, i, quantity);
                Print("BreakBox TP" + (i + 1) + " filled " + quantity + " @ " + price + " -> " + d.Why);
                if (_bracket.QtyOpen > 0)
                {
                    // Re-submit the stop at the NEW quantity. NT8's managed
                    // approach will shrink an oversized exit on its own, but
                    // doing it explicitly is what keeps _lastStopSent, the
                    // bracket and the platform describing the same order.
                    _lastStopSent = double.NaN;
                    SubmitStop(d.StopMoved ? "tier:be" : "tier:resize");
                    DrawLevels();
                }
                return;
            }

            // Any exit that leaves us flat closes the trade out.
            if (_inTrade && Position.MarketPosition == MarketPosition.Flat)
                WentFlat(price);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
                                              int quantity, int filled, double averageFillPrice,
                                              OrderState orderState, DateTime time, ErrorCode error,
                                              string comment)
        {
            string sig = order.Name;

            if (sig == SigLong || sig == SigShort)
                _entryOrder = order;
            else if (sig == SigStop)
            {
                _stopOrder = order;
                if (orderState == OrderState.Working || orderState == OrderState.Accepted)
                    _stopChangePending = false;
            }
            else
            {
                for (int i = 0; i < BbExitConfig.MAX_TIERS; i++)
                    if (sig == TierSig[i]) _tierOrders[i] = order;
            }

            // A rejected bracket leg leaves the position naked. This is the one
            // failure that costs real money, so it flattens rather than retries.
            if (orderState == OrderState.Rejected)
            {
                Print("BreakBox: " + sig + " REJECTED (" + error + ": " + comment + ")");
                if (sig == SigStop || sig == SigTp1 || sig == SigTp2 || sig == SigTp3)
                    FlattenAll("leg_rejected");
                else if (sig == SigLong || sig == SigShort)
                {
                    _entryPending = false;
                    if (_entryFromEngine)
                    {
                        _entryFromEngine = false;
                        OnEntryRejected(_owningEngine, "order_rejected");
                    }
                }
                return;
            }

            // A stop CANCELLED while we are still positioned, and not by us, is
            // a human pulling it in Chart Trader. Respect it permanently.
            if (sig == SigStop && orderState == OrderState.Cancelled && _inTrade && !_flattenPending)
            {
                if (_stopCancelAt == DateTime.MinValue)
                    _stopCancelAt = DateTime.Now;
                else if ((DateTime.Now - _stopCancelAt).TotalSeconds > BracketCancelGraceSec)
                {
                    BbExits.AdoptManualStop(_bracket, _bracket.StopPx, true);
                    Print("BreakBox: stop cancelled by hand — not resubmitting");
                }
            }
        }

        #endregion

        #region Governor

        private void CheckDailyLimits()
        {
            double cum = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            if (double.IsNaN(_dayStartCum))
                _dayStartCum = cum;
            _dayPnl = cum - _dayStartCum;

            int n = SystemPerformance.AllTrades.Count;
            if (n > 0)
            {
                double last = SystemPerformance.AllTrades[n - 1].ProfitCurrency;
                _tradePnls.Add(last);
                if (last > 0) _winsToday++;
                else if (last < 0) _lossesToday++;
            }

            if (DailyLossLimit > 0 && _dayPnl <= -Math.Abs(DailyLossLimit))
                Lockout("daily_loss");
            else if (DailyProfitTarget > 0 && _dayPnl >= Math.Abs(DailyProfitTarget))
                Lockout("daily_target");
        }

        private void Lockout(string why)
        {
            if (_lockout)
                return;
            _lockout = true;
            _lockoutWhy = why;
            Print("BreakBox: LOCKED OUT (" + why + "), day P&L " + _dayPnl.ToString("C2"));
        }

        #endregion

        #region Drawing

        private void DrawBox()
        {
            var box = _engine.Box;
            if (box == null || box.Id == _lastDrawnBoxId)
                return;
            _lastDrawnBoxId = box.Id;

            Brush b = box.Valid ? Brushes.DeepSkyBlue : Brushes.Gray;
            DrawTag(Draw.Rectangle(this, Tag("box"), false, box.AnchorStart, box.Low, Time[0].AddHours(4),
                                   box.High, b, b, 12));
            DrawTag(Draw.Text(this, Tag("boxlab"), box.Valid ? "BOX" : "BOX (out of band)", 0,
                              box.High + 4 * TickSize, b));
        }

        private void DrawLevels()
        {
            if (!ShowLevels || !_inTrade)
                return;
            DrawTag(Draw.HorizontalLine(this, "BB_stop", _bracket.StopPx, Brushes.Red));
            for (int i = 0; i < _bracket.Tiers; i++)
            {
                if (_bracket.TierFilled[i])
                    continue;
                DrawTag(Draw.HorizontalLine(this, "BB_tp" + (i + 1), _bracket.TargetPx[i], Brushes.LimeGreen));
            }
            DrawTag(Draw.HorizontalLine(this, "BB_entry", _bracket.EntryPx, Brushes.White));
        }

        private string Tag(string prefix)
        {
            return prefix + "_" + (_tagSeq++);
        }

        private void DrawTag(object drawn)
        {
            var d = drawn as DrawingTool;
            if (d == null)
                return;
            _drawTags.Add(d.Tag);
            // A strategy that draws two objects a minute for a year is a memory
            // leak with a chart attached.
            while (_drawTags.Count > MaxDrawTags)
            {
                RemoveDrawObject(_drawTags[0]);
                _drawTags.RemoveAt(0);
            }
        }

        private double RoundTick(double px)
        {
            return BbMath.RoundToTick(px, TickSize);
        }

        #endregion

        #region Parameters

        [NinjaScriptProperty, Range(1, int.MaxValue)]
        [Display(Name = "Base quantity", Order = 1, GroupName = "01. Sizing")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "Risk multiplier", Description = "0.5 / 1 / 1.5 on the panel", Order = 2, GroupName = "01. Sizing")]
        public double RiskMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Box source", Order = 1, GroupName = "02. Box")]
        public BbBoxSource BoxSourceParam { get; set; }

        [NinjaScriptProperty, Range(1, 1440)]
        [Display(Name = "HTF minutes", Description = "PriorPeriod slot width", Order = 2, GroupName = "02. Box")]
        public int HtfMinutes { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Session open HHMM", Order = 3, GroupName = "02. Box")]
        public int SessionOpenHhmm { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "IB start HHMM", Order = 4, GroupName = "02. Box")]
        public int IbStartHhmm { get; set; }

        [NinjaScriptProperty, Range(1, 720)]
        [Display(Name = "IB minutes", Order = 5, GroupName = "02. Box")]
        public int IbMinutes { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "Min box range (ATR)", Order = 6, GroupName = "02. Box")]
        public double MinBoxRangeAtr { get; set; }

        [NinjaScriptProperty, Range(0.0, 1000.0)]
        [Display(Name = "Max box range (ATR)", Order = 7, GroupName = "02. Box")]
        public double MaxBoxRangeAtr { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Break engine", Order = 1, GroupName = "03. Engines")]
        public bool EnableBreak { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Retrace engine", Order = 2, GroupName = "03. Engines")]
        public bool EnableRetrace { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow long", Order = 3, GroupName = "03. Engines")]
        public bool AllowLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow short", Order = 4, GroupName = "03. Engines")]
        public bool AllowShort { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Break buffer (ticks)", Order = 5, GroupName = "03. Engines")]
        public int BreakBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require close outside", Description = "OFF trades wicks through the edge", Order = 6, GroupName = "03. Engines")]
        public bool RequireCloseOutside { get; set; }

        [NinjaScriptProperty, Range(5, 3600)]
        [Display(Name = "Trigger life (seconds)", Order = 7, GroupName = "03. Engines")]
        public int TriggerLifeSec { get; set; }

        [NinjaScriptProperty, Range(0.0, 20.0)]
        [Display(Name = "Retrace: extension (ATR)", Order = 8, GroupName = "03. Engines")]
        public double ExtensionAtr { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Retrace: max bars", Order = 9, GroupName = "03. Engines")]
        public int RetraceMaxBars { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Retrace: offset (ticks)", Order = 10, GroupName = "03. Engines")]
        public int RetraceOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop source", Order = 1, GroupName = "04. Stop")]
        public BbStopSource StopSourceParam { get; set; }

        [NinjaScriptProperty, Range(0, 200)]
        [Display(Name = "Stop buffer (ticks)", Order = 2, GroupName = "04. Stop")]
        public int StopBufferTicks { get; set; }

        [NinjaScriptProperty, Range(1, 2000)]
        [Display(Name = "Manual stop (ticks)", Description = "The `Man` source, and the fallback for every other one", Order = 3, GroupName = "04. Stop")]
        public int ManualStopTicks { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "Stop min (ATR)", Order = 4, GroupName = "04. Stop")]
        public double StopMinAtr { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "Stop max (ATR)", Order = 5, GroupName = "04. Stop")]
        public double StopMaxAtr { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Swing strength", Order = 6, GroupName = "04. Stop")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "MA period", Order = 7, GroupName = "04. Stop")]
        public int MaPeriod { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "E50 period", Order = 8, GroupName = "04. Stop")]
        public int E50Period { get; set; }

        [NinjaScriptProperty, Range(1, 3)]
        [Display(Name = "Tier count", Order = 1, GroupName = "05. Targets")]
        public int TierCount { get; set; }

        [NinjaScriptProperty, Range(0.05, 50.0)]
        [Display(Name = "TP1 (R)", Order = 2, GroupName = "05. Targets")]
        public double Tp1R { get; set; }

        [NinjaScriptProperty, Range(0.05, 50.0)]
        [Display(Name = "TP2 (R)", Order = 3, GroupName = "05. Targets")]
        public double Tp2R { get; set; }

        [NinjaScriptProperty, Range(0.05, 50.0)]
        [Display(Name = "TP3 (R)", Order = 4, GroupName = "05. Targets")]
        public double Tp3R { get; set; }

        [NinjaScriptProperty, Range(1, 99)]
        [Display(Name = "TP1 %", Order = 5, GroupName = "05. Targets")]
        public int Tp1Pct { get; set; }

        [NinjaScriptProperty, Range(1, 99)]
        [Display(Name = "TP2 %", Order = 6, GroupName = "05. Targets")]
        public int Tp2Pct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Breakeven on TP1", Order = 7, GroupName = "05. Targets")]
        public bool BreakevenOnTp1 { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Breakeven offset (ticks)", Order = 8, GroupName = "05. Targets")]
        public int BreakevenOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail after TP2", Order = 9, GroupName = "05. Targets")]
        public bool TrailAfterTp2 { get; set; }

        [NinjaScriptProperty, Range(0.1, 20.0)]
        [Display(Name = "Trail (ATR)", Order = 10, GroupName = "05. Targets")]
        public double TrailAtrMult { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Entry window start HHMM", Order = 1, GroupName = "06. Session")]
        public int EntryWindowStartHhmm { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Entry window end HHMM", Order = 2, GroupName = "06. Session")]
        public int EntryWindowEndHhmm { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Flatten HHMM", Order = 3, GroupName = "06. Session")]
        public int FlattenHhmm { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Max trades per box", Order = 4, GroupName = "06. Session")]
        public int MaxTradesPerBox { get; set; }

        [NinjaScriptProperty, Range(1, 1000)]
        [Display(Name = "Max trades per day", Order = 5, GroupName = "06. Session")]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty, Range(0, 1000000)]
        [Display(Name = "Daily loss limit ($)", Description = "0 = off", Order = 6, GroupName = "06. Session")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty, Range(0, 1000000)]
        [Display(Name = "Daily profit target ($)", Description = "0 = off", Order = 7, GroupName = "06. Session")]
        public double DailyProfitTarget { get; set; }

        [NinjaScriptProperty, Range(2, 500)]
        [Display(Name = "ATR period", Order = 8, GroupName = "06. Session")]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show box", Order = 1, GroupName = "07. Visuals")]
        public bool ShowBox { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show levels", Order = 2, GroupName = "07. Visuals")]
        public bool ShowLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show panel", Order = 3, GroupName = "07. Visuals")]
        public bool ShowPanel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show HUD", Order = 4, GroupName = "07. Visuals")]
        public bool ShowHud { get; set; }

        #endregion
    }
}
