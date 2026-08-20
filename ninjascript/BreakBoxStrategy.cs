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

        // Averaging lab (SIM-ONLY). One signal name per level so OnOrderUpdate
        // can fork: a rejected add is survivable, a rejected protective order
        // is not. N is user-defined (1..32), so the name is built, not fixed.
        private const string AddSigPrefix = "BB_Add";
        private const string SigAvgTp = "BB_AvgTp";
        private const string SigOrphanExit = "BB_OrphanExit";

        private static string AddSig(int level)
        {
            return AddSigPrefix + (level + 1);
        }

        // Matches ONLY "BB_Add<digits>" — by construction this cannot collide
        // with SigAvgTp ("BB_AvgTp") or any other BB_* signal.
        private static bool TryAddSigLevel(string sig, out int level)
        {
            level = -1;
            if (sig == null || !sig.StartsWith(AddSigPrefix, StringComparison.Ordinal))
                return false;
            int n;
            if (!int.TryParse(sig.Substring(AddSigPrefix.Length), out n) || n < 1)
                return false;
            level = n - 1;
            return true;
        }

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

        private BbCloudConfig _cloudCfg;
        private BbCloudState _cloudState;
        private BbCloud _cloud;

        private WilderAtr _atr;
        private Ema _ma, _e50;
        // The ribbon. Built ONCE at DataLoaded from the CONVERTED periods: a
        // panel toggle rebuilds the config (BuildConfigs), and an Ema rebuilt
        // with it would be cold — an engine that stops trading for half an
        // hour because somebody clicked a button.
        private Ema _emaFast, _emaSlow, _emaTrend;
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

        // §13 step 1 — count-only instrumentation. The first thing the
        // calibration protocol does is run with orders disabled and count how
        // often each gate blocks; without these counters there is nothing to
        // count and the alternative is guessing constants off P&L, fitting
        // noise twice. A histogram over GateDepth per engine, plus the RAW
        // arm/fill counts the ladder itself cannot show (a budget of one arm
        // per edge reports one arm whatever the tape did — the raw count is
        // what says whether the gates are starved or the budget is binding).
        // Reset at the session roll so a line describes ONE session. None of
        // this may depend on a fill, a position or the order layer.
        private int[] _cloudGateCounts = new int[BbCloud.GateLadder.Length];
        private int[] _boxGateCounts = new int[BbEngine.GateLadder.Length];
        private int _cloudArmedToday, _cloudFilledToday;
        private int _boxArmedToday, _boxFilledToday;
        private int _barsToday;
        private bool _gateSummaryPrinted;

        // ENGINE LOG dedup keys (§9.4). Compared against the STABLE ladder rung
        // name (BbGateReport.Block), never BlockDetail — that carries bar
        // counts and prices that change every bar, and logging THAT every bar
        // would turn the log into exactly the scroll it exists to avoid.
        private string _cloudLastLoggedGate = "";
        private string _boxLastLoggedGate = "";

        // History (§10). The list is the panel's data source and holds EVERY
        // trade of this run, written or not: in a backtest you still want to
        // see the curve the run produced — you just must not let it touch the
        // live file. The guard is about the FILE, not about the chart.
        private readonly List<BbTradeRecord> _history = new List<BbTradeRecord>();
        private string _histPath = "";
        private string _cfgHash = "";
        // §68 amendment. Sticky once true: a drop can never be undone, so the
        // panel needs to know "some history is missing from RAM" even on a bar
        // where the list has since dropped back under the cap again.
        private bool _historyCapped;

        // Panel-driven overrides. The panel writes them, OnBarUpdate reads them.
        // They start as the parameter values and diverge only when a human
        // clicks something.
        private bool _uiAutoTrade = true;
        private bool _uiCloudOn;
        private bool _uiBreakOn, _uiLongOn, _uiShortOn;
        private double _uiRiskMult = 1.0;
        private BbStopSource _uiStopSource;

        // Drawing
        private readonly List<string> _drawTags = new List<string>();
        private int _tagSeq;
        private int _lastDrawnBoxId = -1;

        // Averaging lab state. _avgQty/_avgAvgPx are OUR fill-accumulated
        // position: inside an execution event NT8 has not necessarily updated
        // Position yet ([[nt8-order-event-race]]), so the recompute reads these,
        // never Position directly.
        private AvgConfig _avgCfg;
        private AvgPlan _avgPlan;
        private bool _avgArmed;
        private int _avgQty;
        private double _avgAvgPx;
        private Order _avgTpOrder;
        private volatile bool _avgTpChangePending;
        private DateTime _avgTpSentAt = DateTime.MinValue;
        private double _avgLastTpSent = double.NaN;
        private int _avgTpLastQty;
        private AvgTradeLog _avgRec;
        private string _avgLogPath = "";
        private bool _avgSimBlockPrinted;
        private readonly int[] _avgFireIdx = new int[AvgConfig.MAX_LEVELS];
        // Realised points of the averaging stack, booked against the LIVE
        // average instead of P0 — see the exit-fill block in OnExecutionUpdate.
        private double _avgRealizedPts;

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

                // ---- Box and engines. Every box horizon is a SECONDS
                // parameter (§8) and is converted in BuildConfigs().
                SessionOpenHhmm = 1800;
                BoxLookbackSec = 210;            // C — the measured ~7-bar white rectangle at 30s
                BoxMinBars = 2;                  // a confirmation COUNT, not a horizon
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
                TriggerLifeSec = 120;

                // ---- Cloud (§5.4). M rows are measurements off the reference
                // chart at 30s; G rows are honest guesses, and §13 step 3 tunes
                // at most THREE of them. TriggerLifeSec is seeded above, not
                // here — it is shared with the Break engine.
                EnableCloud = true;
                RibbonFastSec = 300;
                RibbonSlowSec = 690;
                TrendLineSec = 1560;
                TrendSlopeSec = 300;
                TrendSlopeAtr = 0.15;
                RegimeMemorySec = 900;
                PullbackMaxSec = 600;
                MinPullbackSec = 30;
                CloudPullbackBreak = true;
                CloseInRange = 0.60;
                MinBarRangeAtr = 0.20;
                MinLegAtr = 0.35;
                MinBarsBetweenSec = 180;
                TriggerOffsetTicks = 1;

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
                AtrPeriod = 14;

                // ---- Visuals
                ShowBox = true;
                ShowLevels = true;
                ShowPanel = true;

                // ---- Averaging lab (SIM-ONLY). Defaults per the 2026-08-19 spec.
                // G defaults to 150, not 100: the TP floor needs G >= QN*(8*v - c),
                // which is $102.72 on NQ at q0=1, N=2 — a $100 default would
                // refuse to arm out of the box.
                AveragingEnabled = false;
                AveragingMaxAdds = 2;
                AveragingAddQty = 1;
                AveragingBudgetDollars = 0;
                AveragingBudgetFraction = 0.5;
                AveragingTargetProfitDollars = 150;
                AveragingStopBufferTicks = 8;
                AveragingSpacingSource = AvgSpacingSource.Auto;
                AveragingSpacingAtrMult = 1.0;
                AveragingConfirmBars = 1;
                AveragingVolAbortMult = 2.0;
                AveragingNoAddsFinalMinutes = 15;
                AveragingCommissionRt = 5.76;
                AveragingSlippageReserveTicks = 2;
            }
            else if (State == State.Configure)
            {
                _bracket = new BbBracket();
                _engState = new BbEngineState();
                _cloudState = new BbCloudState();

                // Under EntryHandling.AllEntries NT8 SILENTLY ignores any Enter*
                // beyond this budget — without this line every add is discarded
                // and the module looks armed while doing nothing. Set here, not
                // in SetDefaults: property values are settled by Configure.
                EntriesPerDirection = AveragingEnabled ? AveragingMaxAdds + 1 : 1;
            }
            else if (State == State.DataLoaded)
            {
                _barSec = BarSeconds();
                Print("BreakBox: bar ~ " + _barSec + "s (" + _barSecLabel + ")");

                // Load-bearing order: these six mirrors must be synced from
                // their NinjaScriptProperty BEFORE BuildConfigs() runs, because
                // BuildConfigs reads the mirrors, never the properties directly
                // (a panel toggle only has the mirror to flip — see Rebuild()
                // in BreakBoxPanel.cs). C# default-initializes bool fields to
                // false, so syncing after BuildConfigs() silently baked a
                // disabled, directionless engine (EnableBreak/AllowLong/
                // AllowShort all false) into every fresh chart regardless of
                // what the user set the properties to, and stamped the first
                // config-digest bucket with a configuration that never ran.
                // Do not move this below BuildConfigs() again.
                _uiCloudOn = EnableCloud;
                _uiBreakOn = EnableBreak;
                _uiLongOn = AllowLong;
                _uiShortOn = AllowShort;
                _uiRiskMult = RiskMultiplier;
                _uiStopSource = StopSourceParam;

                BuildConfigs();
                _engine = new BbEngine(_cfg, _engState);
                // BbCloud's constructor sizes _cloudState.SlopeBuf from
                // _cloudCfg.TrendSlopeLookback. Nobody else may allocate it: a
                // second allocation elsewhere is how a fixed-size ring silently
                // under-reads a converted lookback.
                _cloud = new BbCloud(_cloudCfg, _cloudState);
                _atr = new WilderAtr(AtrPeriod);
                _ma = new Ema(MaPeriod);
                _e50 = new Ema(E50Period);
                _emaFast = new Ema(_cloudCfg.RibbonFast);
                _emaSlow = new Ema(_cloudCfg.RibbonSlow);
                _emaTrend = new Ema(_cloudCfg.TrendLine);
                _swings = new SwingDetector(SwingStrength);
                OpenHistory();

                _avgLogPath = System.IO.Path.Combine(
                    System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "BreakBox"),
                    "averaging_lab_log.jsonl");

                // v2's scaling contract expresses every horizon in seconds and
                // converts it through BbScale.Bars(_barSec) — a 30s chart is an
                // intended configuration, not "a different experiment" (that was
                // v1's 1m-only assumption and is now false). The only case still
                // worth a warning is a non-time-based series (Tick/Volume/Range):
                // BarSeconds() ESTIMATES _barSec there instead of reading it
                // exactly, so every seconds-based horizon inherits that
                // approximation. The "fallback" flavor of that (too little
                // history to even estimate) already prints its own warning
                // inside BarSeconds() — this only covers the "estimated but
                // usable" flavor, so the two don't double up.
                if (_barSecLabel.StartsWith("est,", StringComparison.Ordinal))
                    Print("BreakBox WARNING: " + BarsPeriod.BarsPeriodType + " series — bar size "
                          + "above is ESTIMATED from history gaps, not exact wall-clock time. Every "
                          + "seconds-based horizon (ATR gates, bar budgets, TriggerLife, ...) converts "
                          + "through it, so all of them inherit that approximation.");
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
            _cfg.EnableBreak = _uiBreakOn;
            _cfg.AllowLong = _uiLongOn;
            _cfg.AllowShort = _uiShortOn;
            // §8/§6.3. Every box horizon is SECONDS on the property surface and
            // is converted HERE, not at DataLoaded: a panel toggle rebuilds this
            // config too, and a rebuild that skipped the conversion would hand
            // the engine raw seconds where it expects bars — the exact defect
            // (a 240-minute box judged against a 14-bar ATR) v1 shipped with.
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
            _cfg.TriggerLife = BbScale.Bars(TriggerLifeSec, _barSec, 1);
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

            // ---- Cloud (§5.4 -> §8). Every horizon arrives in SECONDS and is
            // converted HERE rather than at DataLoaded, because BuildConfigs
            // also runs on every panel toggle (Panel.cs) — a toggle that
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
            _cloudCfg.PullbackBaseEntry = CloudPullbackBreak;
            _cloudCfg.AllowLong = _uiLongOn;
            _cloudCfg.AllowShort = _uiShortOn;

            // The engine holds a reference to _cfg, so a rebuild has to be
            // handed over rather than left dangling on the old object.
            if (_engine != null)
                _engine = new BbEngine(_cfg, _engState);

            // The digest that draws the seam between configurations. Rebuilt
            // HERE rather than at DataLoaded because BuildConfigs is exactly
            // what a panel toggle calls: a hash that only tracked the startup
            // parameters would stamp post-toggle trades as identical to
            // pre-toggle ones, which is the contamination the seam exists to
            // make visible.
            //
            // EVERY NinjaScriptProperty that changes what is traded belongs in
            // this list — not a hand-picked subset. (§68 tried "the important
            // ones" and was still short: TrendLineSec, PullbackMaxSec and a
            // dozen more cloud/box/stop/target/session dials were missing,
            // which is worse than no digest — it merges genuinely different
            // configurations onto one curve while looking authoritative.) The
            // only two exclusions are `risk` and `qty`: both feed
            // `q = BaseQuantity * _uiRiskMult`, scaling size without touching
            // an entry, exit or gate. Both are still listed here and dropped
            // by Canonical's Excluded array, so the exclusion reads as a
            // decision, not an oversight. The three Visuals toggles (ShowBox /
            // ShowLevels / ShowPanel) never reach a trading decision at all and
            // are not listed anywhere.
            _cfgHash = BbHistory.Hash(BbHistory.Canonical(new List<string>
            {
                "risk=" + _uiRiskMult.ToString("0.##", CultureInfo.InvariantCulture),
                "qty=" + BaseQuantity.ToString(CultureInfo.InvariantCulture),

                "cloud=" + (_uiCloudOn ? "1" : "0"),
                "box=" + (_uiBreakOn ? "1" : "0"),
                "long=" + (_uiLongOn ? "1" : "0"),
                "short=" + (_uiShortOn ? "1" : "0"),

                // 02. Box
                "sopen=" + SessionOpenHhmm.ToString(CultureInfo.InvariantCulture),
                "boxlb=" + BoxLookbackSec.ToString(CultureInfo.InvariantCulture),
                "boxminb=" + BoxMinBars.ToString(CultureInfo.InvariantCulture),
                "boxpct=" + BoxRangePctile.ToString("0.###", CultureInfo.InvariantCulture),
                "boxn=" + BoxSampleN.ToString(CultureInfo.InvariantCulture),
                "boxmean=" + BoxMeanSamples.ToString(CultureInfo.InvariantCulture),
                "boxlo=" + BoxValidLo.ToString("0.###", CultureInfo.InvariantCulture),
                "boxhi=" + BoxValidHi.ToString("0.###", CultureInfo.InvariantCulture),
                "boxdead=" + BoxDeadAtr.ToString("0.###", CultureInfo.InvariantCulture),
                "boxage=" + BoxMaxAgeSec.ToString(CultureInfo.InvariantCulture),
                "boxarms=" + BoxArmsPerEdge.ToString(CultureInfo.InvariantCulture),
                "boxcool=" + BoxArmCooldownSec.ToString(CultureInfo.InvariantCulture),

                // 03. Engines — TriggerLifeSec is shared: BuildConfigs feeds
                // the same value into _cfg.TriggerLife (box) AND
                // _cloudCfg.TriggerLife (cloud), so one key covers both.
                "trig=" + TriggerLifeSec.ToString(CultureInfo.InvariantCulture),
                "ribf=" + RibbonFastSec.ToString(CultureInfo.InvariantCulture),
                "ribs=" + RibbonSlowSec.ToString(CultureInfo.InvariantCulture),
                "trend=" + TrendLineSec.ToString(CultureInfo.InvariantCulture),
                "slopelb=" + TrendSlopeSec.ToString(CultureInfo.InvariantCulture),
                "slopeatr=" + TrendSlopeAtr.ToString("0.###", CultureInfo.InvariantCulture),
                "regmem=" + RegimeMemorySec.ToString(CultureInfo.InvariantCulture),
                "pbmax=" + PullbackMaxSec.ToString(CultureInfo.InvariantCulture),
                "pbmin=" + MinPullbackSec.ToString(CultureInfo.InvariantCulture),
                "cloudbase=" + (CloudPullbackBreak ? "1" : "0"),
                "cir=" + CloseInRange.ToString("0.###", CultureInfo.InvariantCulture),
                "minbrange=" + MinBarRangeAtr.ToString("0.###", CultureInfo.InvariantCulture),
                "minleg=" + MinLegAtr.ToString("0.###", CultureInfo.InvariantCulture),
                "minbb=" + MinBarsBetweenSec.ToString(CultureInfo.InvariantCulture),
                "trigoff=" + TriggerOffsetTicks.ToString(CultureInfo.InvariantCulture),

                // 04. Stop
                "stop=" + _uiStopSource,
                "sbuf=" + StopBufferTicks.ToString(CultureInfo.InvariantCulture),
                "manstop=" + ManualStopTicks.ToString(CultureInfo.InvariantCulture),
                "smin=" + StopMinAtr.ToString("0.###", CultureInfo.InvariantCulture),
                "smax=" + StopMaxAtr.ToString("0.###", CultureInfo.InvariantCulture),
                "swing=" + SwingStrength.ToString(CultureInfo.InvariantCulture),
                "maper=" + MaPeriod.ToString(CultureInfo.InvariantCulture),
                "e50per=" + E50Period.ToString(CultureInfo.InvariantCulture),

                // 05. Targets
                "tiers=" + TierCount.ToString(CultureInfo.InvariantCulture),
                "tp1r=" + Tp1R.ToString("0.###", CultureInfo.InvariantCulture),
                "tp2r=" + Tp2R.ToString("0.###", CultureInfo.InvariantCulture),
                "tp3r=" + Tp3R.ToString("0.###", CultureInfo.InvariantCulture),
                "tp1pct=" + Tp1Pct.ToString(CultureInfo.InvariantCulture),
                "tp2pct=" + Tp2Pct.ToString(CultureInfo.InvariantCulture),
                "be=" + (BreakevenOnTp1 ? "1" : "0"),
                "beoff=" + BreakevenOffsetTicks.ToString(CultureInfo.InvariantCulture),
                "trail=" + (TrailAfterTp2 ? "1" : "0"),
                "trailatr=" + TrailAtrMult.ToString("0.###", CultureInfo.InvariantCulture),

                // 06. Session
                "ews=" + EntryWindowStartHhmm.ToString(CultureInfo.InvariantCulture),
                "ewe=" + EntryWindowEndHhmm.ToString(CultureInfo.InvariantCulture),
                "flat=" + FlattenHhmm.ToString(CultureInfo.InvariantCulture),
                "maxbox=" + MaxTradesPerBox.ToString(CultureInfo.InvariantCulture),
                "maxday=" + MaxTradesPerDay.ToString(CultureInfo.InvariantCulture),
                "dloss=" + DailyLossLimit.ToString("0.##", CultureInfo.InvariantCulture),
                "dprofit=" + DailyProfitTarget.ToString("0.##", CultureInfo.InvariantCulture),
                "atr=" + AtrPeriod.ToString(CultureInfo.InvariantCulture),

                // 07. Averaging lab (§7). The module ON and the module OFF are
                // two different strategies — sharing one digest would blend
                // their journal curves into a single line that describes
                // neither. AveragingEnabled leads because it is the switch;
                // the rest change the grid the switch turns on.
                "avg=" + (AveragingEnabled ? "1" : "0"),
                "avgmax=" + AveragingMaxAdds.ToString(CultureInfo.InvariantCulture),
                "avgq=" + AveragingAddQty.ToString(CultureInfo.InvariantCulture),
                "avgbuddol=" + AveragingBudgetDollars.ToString("0.##", CultureInfo.InvariantCulture),
                "avgbud=" + AveragingBudgetFraction.ToString("0.###", CultureInfo.InvariantCulture),
                "avgg=" + AveragingTargetProfitDollars.ToString("0.##", CultureInfo.InvariantCulture),
                "avgsbuf=" + AveragingStopBufferTicks.ToString(CultureInfo.InvariantCulture),
                "avgspace=" + AveragingSpacingSource,
                "avgatr=" + AveragingSpacingAtrMult.ToString("0.###", CultureInfo.InvariantCulture),
                "avgconf=" + AveragingConfirmBars.ToString(CultureInfo.InvariantCulture),
                "avgvol=" + AveragingVolAbortMult.ToString("0.###", CultureInfo.InvariantCulture),
                "avgcut=" + AveragingNoAddsFinalMinutes.ToString(CultureInfo.InvariantCulture),
                "avgcomm=" + AveragingCommissionRt.ToString("0.##", CultureInfo.InvariantCulture),
                "avgslip=" + AveragingSlippageReserveTicks.ToString(CultureInfo.InvariantCulture),

                "bar=" + BarSeconds().ToString(CultureInfo.InvariantCulture)
            }));
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
            // §5.2 step 0 — the ribbon absorbs THIS closed bar BEFORE any gate
            // reads it. Under the other convention `close > eF` compares a close
            // against an EMA that has not seen it yet, which is a materially
            // looser reclaim test, not a rounding difference.
            _emaFast.Update(bar.Close);
            _emaSlow.Update(bar.Close);
            _emaTrend.Update(bar.Close);

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
            _barsToday++;

            // §13 step 1. Printed once per session at FlattenHhmm, independent
            // of TimeToFlatten/_inTrade below: that check returns false with no
            // position open, and the whole point of this line is running with
            // NO submissions at all — gating it on a position would print
            // nothing in exactly the mode it exists for.
            if (!_gateSummaryPrinted && secs >= BbMath.HhmmToSecs(FlattenHhmm))
            {
                PrintGateSummary();
                _gateSummaryPrinted = true;
            }

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

                // The verdict on a stop cancel we did not ask for (raised in
                // OnOrderUpdate). Deferred on purpose: the alarm fires on the
                // Cancelled event, but a cancel-replace is TWO events, and
                // judging on the first one convicts our own machinery. After
                // the grace window, a working stop means the position got
                // re-covered — by our resubmit, or by the human dragging the
                // stop, which NT8 may deliver as a cancel-replace that
                // re-adopts under the same signal name. A move must never read
                // as a pull. Only a still-uncovered position is a real pull.
                if (_stopCancelAt != DateTime.MinValue
                    && (DateTime.Now - _stopCancelAt).TotalSeconds > BracketCancelGraceSec
                    && !_stopChangePending && !_bracket.StopCancelled)
                {
                    bool covered = _stopOrder != null
                                   && (_stopOrder.OrderState == OrderState.Working
                                       || _stopOrder.OrderState == OrderState.Accepted);
                    if (!covered)
                    {
                        BbExits.AdoptManualStop(_bracket, _bracket.StopPx, true);
                        Print("BreakBox: stop cancelled by hand — not resubmitting");
                    }
                    _stopCancelAt = DateTime.MinValue;
                }

                var d = BbExits.OnBarClose(_exitCfg, _bracket, bar, _atr.Value);
                if (d.StopMoved)
                    SubmitStop("bar:" + d.Why);
                DrawLevels();

                if (_avgArmed)
                    AvgOnBar(bar, secs);
            }

            if (TimeToFlatten(secs))
            {
                FlattenAll("session_window");
                UpdatePanelStatus();
                return;
            }

            bool canTrade = _uiAutoTrade && !_lockout && _atr.IsWarm
                            && BbExits.StopSourceWarm(_exitCfg, _ma.IsWarm, _e50.IsWarm);
            bool positioned = _inTrade || _entryPending;

            // §4.1 — ONE live trigger and ONE position across both engines, and a
            // FIXED order: Cloud, then Box. The first Fire wins and the other is
            // not evaluated on that bar. Two engines racing for one position is
            // undefined behaviour, and undefined behaviour gets invented by
            // whoever implements it next.
            //
            // Warm means EVERY indicator the cloud reads. It cannot ride on
            // canTrade: §5.2 step 1b requires steps 2-4 to keep running with
            // auto-trade off, and a cold ribbon EMA has to suppress all five
            // gold-candle gates on its own, not just the ones canTrade covers.
            bool cloudWarm = _atr.IsWarm && _emaFast.IsWarm && _emaSlow.IsWarm && _emaTrend.IsWarm;

            // The cloud has no clock of its own for the entry window — it takes
            // canTrade/positioned as suppression flags and nothing else. The box
            // engine tests its own window internally (Core.cs), so this is
            // folded in only for the cloud call.
            bool windowOpen = BbMath.InWindow(secs, BbMath.HhmmToSecs(EntryWindowStartHhmm),
                                                    BbMath.HhmmToSecs(EntryWindowEndHhmm));

            BbAction a = default(BbAction);
            if (_uiCloudOn)
            {
                a = _cloud.OnBar(bar, secs, _emaFast.Value, _emaSlow.Value, _emaTrend.Value,
                                 _atr.Value, cloudWarm, canTrade && windowOpen, positioned);
                // §13 step 1. Read straight off the engine's own return/state —
                // never off a fill or a position — so this counts correctly
                // even with submissions disabled.
                CountGate(_cloudGateCounts, _cloudState.Gate.GateDepth);
                if (a.Fire) _cloudArmedToday++;
                LogGateTransition("cloud", _cloudState.Gate, ref _cloudLastLoggedGate);
            }
            if (!a.Fire)
            {
                a = _engine.OnBar(bar, secs, sessionDate, _atr.Value, _atr.IsWarm, canTrade, positioned);
                CountGate(_boxGateCounts, _engState.Gate.GateDepth);
                if (a.Fire) _boxArmedToday++;
                LogGateTransition("box", _engState.Gate, ref _boxLastLoggedGate);
            }
            else if (_uiBreakOn)
            {
                // §4.1 — cloud fired first, so the box never got a bar to run
                // on this time: its ladder above is now STALE until it is
                // evaluated again. Worth its own line, or "why did the box
                // just go quiet" has no answer in the scrollback.
                EngineLog("box suppressed — cloud armed this bar");
            }

            if (ShowBox) DrawBox();

            // canTrade is no longer re-tested here — each engine owns that
            // decision now. The position check stays: SubmitEntry while
            // positioned is the one mistake that costs real money, and it is
            // cheap to refuse twice.
            if (a.Fire && !positioned)
            {
                EngineLog((a.Engine == BbEntryEngine.Cloud ? "cloud" : "box") + " armed "
                          + (a.Dir > 0 ? "long" : "short") + " @ "
                          + a.TriggerPx.ToString("0.00", CultureInfo.InvariantCulture));
                SubmitEntry(a);
            }
            else if (_entryPending)
                AgeWorkingEntry();

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

            // §13 step 1 instrumentation resets alongside the daily counters
            // above, so a printed line describes ONE session.
            Array.Clear(_cloudGateCounts, 0, _cloudGateCounts.Length);
            Array.Clear(_boxGateCounts, 0, _boxGateCounts.Length);
            _cloudArmedToday = 0; _cloudFilledToday = 0;
            _boxArmedToday = 0; _boxFilledToday = 0;
            _barsToday = 0;
            _gateSummaryPrinted = false;
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

        // §13 step 1 — the histogram increment. A depth of -1 (the engine
        // fired, nothing blocked) is not a bucket and is skipped on purpose:
        // the sum of this array is "bars blocked", not "bars evaluated".
        private static void CountGate(int[] counts, int depth)
        {
            if (depth >= 0 && depth < counts.Length)
                counts[depth]++;
        }

        // ENGINE LOG — the "what changed" line for an engine's WHY NO TRADE
        // ladder. Fires only when the blocking rung actually MOVES to a
        // different one; a clear gate (Block == "") resets the memory but is
        // not itself logged — that moment is the trigger-armed line at the
        // SubmitEntry call site instead. Two engines, two independent memories:
        // BbLogRing's own dedup is a single last-text slot, and alternating
        // cloud/box lines into it every bar would defeat it.
        private void LogGateTransition(string label, BbGateReport gate, ref string lastLogged)
        {
            string block = gate == null ? "" : gate.Block;
            if (block == lastLogged)
                return;
            lastLogged = block;
            if (block.Length > 0)
                EngineLog(label + ": " + block);
        }

        private void PrintGateSummary()
        {
            PrintGateLine("CLOUD", BbCloud.GateLadder, _cloudGateCounts, _cloudArmedToday, _cloudFilledToday);
            PrintGateLine("BOX  ", BbEngine.GateLadder, _boxGateCounts, _boxArmedToday, _boxFilledToday);
        }

        // Names every rung straight off the engine's own GateLadder so the
        // printed label can never drift from the depth it names (§9.3's rule
        // for the panel applies here too).
        private void PrintGateLine(string label, string[] ladder, int[] counts, int armed, int filled)
        {
            string line = label + " " + _sessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                         + "  bars=" + _barsToday;
            for (int i = 0; i < ladder.Length; i++)
                line += "  " + ladder[i].Replace(' ', '-') + "=" + counts[i];
            line += "  |  armed=" + armed + " filled=" + filled;
            Print(line);
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

            // A stop entry is a BREAKOUT: it has to rest on the far side of the
            // market. Calculate.OnBarClose means this level was measured on the
            // bar that just closed, so in a continuing move price can already be
            // through it by the time the order is accepted — and NT8 refuses a
            // sell stop above the market (or a buy stop below it) with a modal
            // error that also stalls Playback until it is dismissed. SubmitStop
            // has carried this guard since v1; the entry path never did.
            // Declining is the honest action, not chasing: if price is already
            // past the level, the move we wanted to join happened without us.
            if (!a.IsLimit)
            {
                double side = a.Dir > 0 ? GetCurrentAsk() : GetCurrentBid();
                if (BbMath.StopThroughMarket(a.TriggerPx, side, a.Dir))
                {
                    OnEntryRejected(a.Engine, "trigger_through_market");
                    return;
                }
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

        // The ONE place an entry becomes a live trade. Extracted because it has
        // two legitimate callers: the ordinary full fill, and the PARTIAL fill
        // that CancelWorkingEntry and OnOrderUpdate find when a trigger is
        // pulled or rejected after some contracts already traded. Both have to
        // produce a bracket — a position without one is the most expensive state
        // this strategy can reach, and IgnoreAllErrors means nobody would say so.
        //
        // The _inTrade guard makes it idempotent: cancelling a part-filled order
        // can race the remainder filling, and adopting twice would open a second
        // bracket over the same position.
        private void AdoptEntryFill(double px, int filled, DateTime time)
        {
            if (filled < 1 || _inTrade)
                return;

            _entryPending = false;
            _entryFromEngine = false;
            _entryBarsWaiting = 0;
            _inTrade = true;
            // What actually traded, not what was asked for: on a partial the
            // bracket, the panel and the journal all have to agree with the
            // position, and _qty is what they read.
            _qty = filled;
            _entryFillPx = px;
            _entryTime = time;
            EngineLog("filled " + (_dir > 0 ? "long" : "short") + " " + filled
                      + " @ " + px.ToString("0.00", CultureInfo.InvariantCulture));
            // The fill is what really spends the token/edge, and it belongs
            // to the owner (§4.1). Routing this unconditionally to the box
            // engine would spend the WRONG engine's memory on a cloud fill —
            // the box would count a trade it never took, and the cloud's
            // token/cooldown would never reset.
            if (_owningEngine == BbEntryEngine.Cloud) { _cloud.OnEntryFilled(); _cloudFilledToday++; }
            else { _engine.OnEntryFilled(); _boxFilledToday++; }
            _tradesToday++;
            OpenBracket(px, filled);
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
            _entryBarsWaiting++;                // display only for the box path; the cloud path below decides on it
            if (!_entryFromEngine)
                return;

            if (_owningEngine == BbEntryEngine.Cloud)
            {
                // Unlike the box engine, BbCloud carries no trigger-life clock of
                // its own — nothing inside BbCloud.OnBar increments
                // TriggerArmedBars against _cloudCfg.TriggerLife. §5.2 step 7
                // says the SHELL is the one that watches the working order's age
                // and cancels it; this is that clock.
                if (_cloudCfg != null && _entryBarsWaiting > _cloudCfg.TriggerLife)
                    CancelWorkingEntry("expired", true);
                return;
            }

            if (_engine == null || _engine.BreakArmed)
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

            // A PART-FILLED entry is a live position, and the guard below could
            // not see one: it only cancels an order in Working/Accepted, and
            // PartFilled is neither — it then dropped the reference. Since
            // OnExecutionUpdate ignores anything that is not OrderState.Filled,
            // those contracts got no stop and no target while the shell believed
            // it was flat, so nothing would ever close them. Adopt what traded,
            // cancel the remainder. No refund to the engine: the trade happened.
            if (_entryOrder != null && _entryOrder.Filled > 0 && !_inTrade)
            {
                Order po = _entryOrder;
                _entryOrder = null;
                EngineLog("disarm: " + why + " — adopting " + po.Filled + " already filled");
                CancelOrder(po);
                AdoptEntryFill(po.AverageFillPrice, po.Filled, Time[0]);
                return;
            }

            EngineLog("disarm: " + why);
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
                if (_owningEngine == BbEntryEngine.Cloud && _cloud != null)
                    _cloud.OnTriggerExpired();
                else if (_engine != null)
                    _engine.OnTriggerExpired();
            }
            else
            {
                OnEntryRejected(_owningEngine, why);
            }
        }

        // The single refusal callback. It routes on the OWNING engine (§4.1).
        private void OnEntryRejected(BbEntryEngine engine, string reason)
        {
            Print("BreakBox: entry refused (" + reason + ")");
            if (engine == BbEntryEngine.Cloud && _cloud != null)
                _cloud.OnEntryRejected(reason);
            else if (_engine != null)
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
            // Per-trade, and first: an alarm belongs to the trade that raised
            // it. Both bracket flavours come through here.
            _stopCancelAt = DateTime.MinValue;

            if (AveragingEnabled)
            {
                string why;
                AvgPlan plan = TryArmAveraging(fillPx, qty, out why);
                if (plan != null)
                {
                    OpenAveragingBracket(fillPx, qty, plan);
                    return;
                }
                // A refusal is never silent and never blocks the trade.
                Print("BreakBox AVG: not armed — " + why + " — trade runs the normal bracket");
            }

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

        // Every reason NOT to average, checked in cheap-to-expensive order.
        // Returns null with `why` set, or an armed plan.
        private AvgPlan TryArmAveraging(double fillPx, int qty, out string why)
        {
            // SIM-ONLY guard (spec §4): live accounts never arm, no override.
            // Fails CLOSED on an unverifiable (null) Account — never treat
            // "can't tell" as "safe to arm".
            if (State == State.Realtime
                && (Account == null
                    || (!Account.Name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)
                        && !Account.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase))))
            {
                if (!_avgSimBlockPrinted)
                {
                    _avgSimBlockPrinted = true;
                    Print("BreakBox AVG: account '" + (Account != null ? Account.Name : "unknown account")
                          + "' is not Sim/Playback — averaging lab is SIM-ONLY and stays OFF");
                }
                why = "live_account";
                return null;
            }

            // No arming inside the pre-close cutoff: a dug grid meeting the
            // session flatten is a certain -L on the whole stack.
            int secs = Time[0].Hour * 3600 + Time[0].Minute * 60 + Time[0].Second;
            if (secs >= BbMath.HhmmToSecs(FlattenHhmm) - AveragingNoAddsFinalMinutes * 60)
            {
                why = "inside_close_cutoff";
                return null;
            }

            // The trade's slice of what is LEFT of today's budget — a later
            // trade in a losing day gets a smaller grid automatically. A
            // direct dollar budget (AveragingBudgetDollars > 0) overrides the
            // fraction, but either way the day-left cap binds: one trade may
            // never out-risk the day.
            double dayLossSoFar = _dayPnl < 0 ? -_dayPnl : 0.0;
            double dayLeft = DailyLossLimit - dayLossSoFar;
            double lArm = AveragingBudgetDollars > 0.0
                ? Math.Min(AveragingBudgetDollars, dayLeft)
                : Math.Min(AveragingBudgetFraction * DailyLossLimit, dayLeft);
            if (lArm <= 0.0)
            {
                why = "no_day_budget_left";
                return null;
            }
            // Visibility: a direct budget silently capped by the day reads as a solver bug.
            if (AveragingBudgetDollars > 0.0 && lArm < AveragingBudgetDollars)
                Print(string.Format(CultureInfo.InvariantCulture,
                    "BreakBox AVG: budget {0:0.##} capped by remaining daily loss ({1:0.##} of {2:0.##} left) — raise Daily loss limit (06. Session) to use the full budget",
                    AveragingBudgetDollars, dayLeft, DailyLossLimit));

            if (!_atr.IsWarm || _atr.Value <= 0.0)
            {
                why = "atr_cold";
                return null;
            }

            // Structural spacing candidate (Arm takes the min with the budget d,
            // so structure only ever compresses the grid).
            var box = _engState != null ? _engState.Box : null;
            bool boxUsable = box != null && box.High > box.Low;
            bool useBox;
            switch (AveragingSpacingSource)
            {
                case AvgSpacingSource.BoxHeight:
                    if (!boxUsable)
                    {
                        why = "no_box_for_spacing";
                        return null;
                    }
                    useBox = true;
                    break;
                case AvgSpacingSource.AtrMult:
                    useBox = false;
                    break;
                default: // Auto
                    useBox = _owningEngine == BbEntryEngine.Break && boxUsable;
                    break;
            }
            double dStructTicks = useBox
                ? (box.High - box.Low) / TickSize
                : AveragingSpacingAtrMult * _atr.Value / TickSize;
            string spacing = useBox ? "box" : "atr";

            _avgCfg = new AvgConfig();
            _avgCfg.TickSize = TickSize;
            _avgCfg.TickValue = Instrument.MasterInstrument.PointValue * TickSize;
            _avgCfg.MaxAdds = AveragingMaxAdds;
            _avgCfg.AddQty = AveragingAddQty;
            _avgCfg.BudgetDollars = lArm;
            _avgCfg.TargetDollars = AveragingTargetProfitDollars;
            _avgCfg.StopBufferTicks = AveragingStopBufferTicks;
            _avgCfg.CommissionRt = AveragingCommissionRt;
            _avgCfg.SlippageReserveTicks = AveragingSlippageReserveTicks;
            _avgCfg.ConfirmBars = AveragingConfirmBars;
            _avgCfg.VolAbortMult = AveragingVolAbortMult;

            AvgPlan plan = AvgEngine.Arm(_avgCfg, _dir, fillPx, qty, dStructTicks, _atr.Value);
            if (!plan.Armed)
            {
                why = plan.RefusedWhy;
                return null;
            }

            _avgRec = new AvgTradeLog();
            _avgRec.EntryTs = _entryTime;
            _avgRec.Dir = _dir;
            _avgRec.Engine = _owningEngine.ToString();
            _avgRec.EntryPx = fillPx;
            _avgRec.EntryQty = qty;
            _avgRec.DTicks = plan.DTicks;
            _avgRec.STicks = plan.STicks;
            _avgRec.Levels = plan.Levels;
            _avgRec.LArm = lArm;
            _avgRec.LEff = plan.LEff;
            _avgRec.G = AveragingTargetProfitDollars;
            _avgRec.StopPx = plan.StopPx;
            _avgRec.SpacingSource = spacing;
            _avgRec.Fills.Add(new AvgFillRec { Ts = _entryTime, Level = -1, PlannedPx = fillPx, FillPx = fillPx, Qty = qty });

            why = "";
            return plan;
        }

        // The averaging bracket: ONE stop for the whole stack (SubmitStop, with
        // fromEntrySignal "" while armed) and ONE dynamic TP. The 3-tier bracket
        // is never armed for this trade — _bracket.Tiers stays 0, which keeps
        // SubmitTier, OnTierFill and the breakeven/trail paths inert by
        // construction, while WentFlat/journal/panel read the same fields they
        // always read.
        private void OpenAveragingBracket(double fillPx, int qty, AvgPlan plan)
        {
            _avgArmed = true;
            _avgPlan = plan;
            _avgQty = qty;
            _avgAvgPx = fillPx;
            _avgRealizedPts = 0.0;
            _avgSimBlockPrinted = false;

            _bracket.Dir = _dir;
            _bracket.EntryPx = fillPx;
            _bracket.AtrRef = _atr.IsWarm ? _atr.Value : 0.0;
            _bracket.Mfe = fillPx;
            _bracket.QtyTotal = qty;
            _bracket.QtyOpen = qty;
            _bracket.BeApplied = true;          // no tiers -> inert, but explicit
            _bracket.TrailArmed = false;
            _bracket.RealizedPts = 0.0;
            _bracket.QtyClosed = 0;
            _bracket.StopCancelled = false;
            _bracket.BarsInTrade = 0;
            _bracket.Tiers = 0;
            for (int i = 0; i < BbExitConfig.MAX_TIERS; i++)
            {
                _bracket.TargetPx[i] = 0.0;
                _bracket.TierFilled[i] = false;
            }
            _bracket.StopPx = plan.StopPx;
            _bracket.InitialStopPx = plan.StopPx;
            _bracket.R = Math.Abs(fillPx - plan.StopPx);
            _bracket.StopWhy = "avg_grid";

            var u = AvgEngine.OnFill(_avgCfg, plan, fillPx, qty);
            _bracket.StopPx = u.StopPx;

            Print(string.Format(CultureInfo.InvariantCulture,
                "BreakBox AVG armed {0} q0={1} d={2}t s={3}t levels={4} LArm={5:0.##} LEff={6:0.##} stop {7} tp {8} ({9})",
                _dir > 0 ? "LONG" : "SHORT", qty, plan.DTicks, plan.STicks, plan.Levels,
                _avgRec.LArm, plan.LEff, _bracket.StopPx, u.TpPx, _avgRec.SpacingSource));

            SubmitStop("avg:init");
            SubmitAvgTp(u.TpPx, "init");
            DrawLevels();
        }

        // The whole-stack dynamic TP. Cancel-replace by reference: the ref is
        // nulled BEFORE the resubmit so our own in-stack Cancelled echo can never
        // be misread ([[latigobreak-project]] commit 575c524 pattern).
        private void SubmitAvgTp(double px, string why)
        {
            if (!_inTrade || !_avgArmed)
                return;
            int qty = _bracket.QtyOpen;
            if (qty < 1)
                return;
            if (!double.IsNaN(_avgLastTpSent) && Math.Abs(px - _avgLastTpSent) < TickSize / 2.0
                && qty == _avgTpLastQty)
                return;

            _avgTpOrder = null;
            _avgTpChangePending = true;
            _avgTpSentAt = DateTime.Now;
            _avgLastTpSent = px;
            _avgTpLastQty = qty;

            if (_dir > 0) ExitLongLimit(0, true, qty, px, SigAvgTp, "");
            else ExitShortLimit(0, true, qty, px, SigAvgTp, "");
        }

        // The averaging bar loop: TP watchdog, one-way aborts, then the
        // confirmation machine. Adds are MARKET orders fired here, on the main
        // thread at bar close — never from OnMarketData
        // ([[nt8-orders-from-marketdata-thread-crash]]).
        private void AvgOnBar(BbBar bar, int secs)
        {
            if (_avgPlan == null || _avgRec == null)
                return;

            // Telemetry: the compact bar path (offline counterfactuals) and the
            // prop-firm axis (worst open P&L, intrabar extremes).
            if (_avgRec.Bars.Count < AvgTradeLog.MAX_BARS)
                _avgRec.Bars.Add(new AvgBarRec { Ts = bar.Time, High = bar.High, Low = bar.Low, Close = bar.Close });
            else
                _avgRec.BarsCapped = true;
            double worstPx = _dir > 0 ? bar.Low : bar.High;
            double openPnl = (worstPx - _avgAvgPx) * _dir * _avgQty * Instrument.MasterInstrument.PointValue;
            if (openPnl < _avgRec.MinUnrealized)
                _avgRec.MinUnrealized = openPnl;

            // TP watchdog — same shape as the stop's (line ~681).
            if (_avgTpChangePending
                && (DateTime.Now - _avgTpSentAt).TotalSeconds > ExitChangeWatchdogSec)
            {
                Print("BreakBox AVG: TP modify unacknowledged after " + ExitChangeWatchdogSec
                      + "s — resubmitting");
                _avgTpChangePending = false;
                double px = _avgLastTpSent;
                _avgLastTpSent = double.NaN;
                _avgTpLastQty = 0;
                SubmitAvgTp(px, "watchdog");
            }

            // One-way aborts: volatility expansion, then the pre-close cutoff.
            if (AvgEngine.VolAbort(_avgCfg, _avgPlan, _atr.Value))
            {
                _avgRec.AddsAborted = true;
                _avgRec.AbortWhy = "vol_expansion";
                Print(string.Format(CultureInfo.InvariantCulture,
                    "BreakBox AVG: vol abort — ATR {0:0.##} > {1:0.#}x entry ATR {2:0.##}; remaining adds dead, position and stop stay",
                    _atr.Value, AveragingVolAbortMult, _avgPlan.EntryAtr));
            }
            if (!_avgPlan.AddsAborted
                && secs >= BbMath.HhmmToSecs(FlattenHhmm) - AveragingNoAddsFinalMinutes * 60)
            {
                _avgPlan.AddsAborted = true;
                _avgRec.AddsAborted = true;
                _avgRec.AbortWhy = "close_cutoff";
                Print("BreakBox AVG: inside the pre-close cutoff — no more adds this trade");
            }

            int n = AvgEngine.OnBarClosed(_avgCfg, _avgPlan, bar.High, bar.Low, bar.Close, _avgFireIdx);
            for (int i = 0; i < n; i++)
                SubmitAdd(_avgFireIdx[i]);
        }

        private void SubmitAdd(int level)
        {
            int q = AveragingAddQty;
            string sig = AddSig(level);
            Print(string.Format(CultureInfo.InvariantCulture,
                "BreakBox AVG: level {0} confirmed @ {1} — adding {2} at market ({3})",
                level + 1, _avgPlan.LevelPx[level], q, sig));
            if (_dir > 0) EnterLong(0, q, sig);
            else EnterShort(0, q, sig);
        }

        private void OnAddExecution(int level, double price, int qty, DateTime time)
        {
            _avgAvgPx = (_avgAvgPx * _avgQty + price * qty) / (_avgQty + qty);
            _avgQty += qty;
            _bracket.QtyTotal = _avgQty;
            _bracket.QtyOpen = _avgQty - _bracket.QtyClosed;

            _avgRec.Fills.Add(new AvgFillRec
            {
                Ts = time, Level = level,
                PlannedPx = _avgPlan.LevelPx[level], FillPx = price, Qty = qty
            });

            var u = AvgEngine.OnFill(_avgCfg, _avgPlan, _avgAvgPx, _avgQty);
            _bracket.StopPx = u.StopPx;

            Print(string.Format(CultureInfo.InvariantCulture,
                "BreakBox AVG: add{0} fill {1} @ {2} -> qty {3} avg {4} stop {5}{6} tp {7}",
                level + 1, qty, price, _avgQty, _avgAvgPx, u.StopPx,
                u.StopMoved ? " (budget ratchet)" : "", u.TpPx));

            _lastStopSent = double.NaN;                 // defeat the dedupe, tier-resize idiom
            SubmitStop("avg:add");
            SubmitAvgTp(u.TpPx, "add");
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

            // Cancel-replace by reference, same as SubmitAvgTp: the ref is
            // dropped BEFORE the submit, so the Cancelled event our own replace
            // produces carries an order that matches nothing and the hand-pull
            // detector in OnOrderUpdate cannot misread it. It goes here and not
            // at the top of the method on purpose — an early return above would
            // otherwise leave us holding no reference to a stop that is still
            // working.
            _stopOrder = null;
            _stopChangePending = true;
            _stopChangeSentAt = DateTime.Now;
            _lastStopSent = px;

            string fromSig = _avgArmed ? "" : _entrySig;
            if (_dir > 0) ExitLongStopMarket(0, true, qty, px, SigStop, fromSig);
            else ExitShortStopMarket(0, true, qty, px, SigStop, fromSig);
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
                // Same fork as SubmitStop, for the same reason: a named
                // fromEntrySignal closes only THAT entry's quantity, and an
                // averaging stack was built under BB_Long + one BB_Add<N> per level.
                // Naming the entry here left every add in the market, unstopped,
                // after a session flatten or a rejected leg.
                string fromSig = _avgArmed ? "" : _entrySig;
                if (_dir > 0) ExitLong(SigFlatten, fromSig);
                else ExitShort(SigFlatten, fromSig);
            }
            else
            {
                _flattenPending = false;
            }
        }

        // The stop and every tier limit belonging to the trade that just ended.
        // Anything not already in a terminal state gets cancelled: PartFilled is
        // neither Working nor Accepted, and omitting it is the same oversight that
        // once abandoned a part-filled entry as a naked position.
        private void CancelBracketLegs()
        {
            CancelIfLive(_stopOrder);
            CancelIfLive(_avgTpOrder);
            for (int i = 0; i < BbExitConfig.MAX_TIERS; i++)
                CancelIfLive(_tierOrders[i]);
        }

        private void CancelIfLive(Order o)
        {
            if (o == null)
                return;
            if (o.OrderState != OrderState.Filled && o.OrderState != OrderState.Cancelled
                && o.OrderState != OrderState.Rejected)
                CancelOrder(o);
        }

        private static bool IsTierSig(string sig)
        {
            for (int i = 0; i < TierSig.Length; i++)
                if (sig == TierSig[i])
                    return true;
            return false;
        }

        private void WentFlat(double exitPx)
        {
            // Realised points, per fill. Anything we never saw an execution for
            // — a hand flatten in Chart Trader, NT8 closing us out itself — is a
            // RESIDUAL valued at the price that ended the trade, not the whole
            // position valued there.
            double pts = _bracket.RealizedPts;
            int residual = _bracket.QtyTotal - _bracket.QtyClosed;
            if (residual > 0)
                pts += (exitPx - _bracket.EntryPx) * _bracket.Dir * residual;
            double pnl = pts * Instrument.MasterInstrument.PointValue;

            // The same arithmetic on the averaging basis. Both survive: `pts`
            // keeps pricing every non-averaging trade exactly as before, and the
            // journal reaches for the average-basis pair only when the trade
            // really was a stack.
            double avgPts = 0.0, avgPnl = 0.0;
            if (_avgArmed)
            {
                avgPts = _avgRealizedPts;
                if (residual > 0)
                    avgPts += (exitPx - _avgAvgPx) * _bracket.Dir * residual;
                avgPnl = avgPts * Instrument.MasterInstrument.PointValue;
            }

            // Journalled BEFORE the bracket is torn down: `_bracket.Dir` is
            // zeroed twelve lines below, and reading it after is how a history
            // file fills up with dir=0 rows that plot but mean nothing.
            BbTradeRecord rec = default(BbTradeRecord);
            rec.Ts = Time[0];
            rec.Dir = _bracket.Dir;
            rec.Entry = _bracket.EntryPx;
            rec.Exit = BbExits.ExitPxFromPts(_bracket.EntryPx, _bracket.Dir, pts, _bracket.QtyTotal);
            rec.Qty = _bracket.QtyTotal;
            rec.R = BbExits.RFromPts(pts, _bracket.QtyTotal, _bracket.R);
            rec.Pnl = pnl;
            rec.Engine = _owningEngine.ToString();
            rec.ExitReason = _exitReason.Length > 0 ? _exitReason : "unknown";
            rec.CfgHash = _cfgHash;
            if (_avgArmed)
            {
                // Entry stays P0 — that IS where the trade started — but the cash
                // and the single exit price that reproduces it come off the
                // average basis, the only one that matches the account.
                rec.Pnl = avgPnl;
                rec.Exit = BbExits.ExitPxFromPts(_avgAvgPx, _bracket.Dir, avgPts, _bracket.QtyTotal);
            }
            AppendHistory(rec);

            if (_avgArmed)
            {
                _avgRec.Pnl = avgPnl;
                _avgRec.Outcome = _exitReason == SigAvgTp ? "tp"
                                : _exitReason == SigStop ? "stop"
                                : (_exitReason == "session_window" || _exitReason == SigFlatten) ? "session_flatten"
                                : "other";
                if (State == State.Realtime)        // the lab logs live sims only; backtests stay off the file
                {
                    try
                    {
                        System.IO.File.AppendAllText(_avgLogPath, AvgLog.Serialise(_avgRec) + Environment.NewLine);
                    }
                    catch (Exception ex)
                    {
                        Print("BreakBox AVG: lab log NOT written (" + ex.Message + ")");
                    }
                }
                Print("BreakBox AVG: trade closed — " + _avgRec.Outcome + ", pnl "
                      + avgPnl.ToString("C2") + ", min open " + _avgRec.MinUnrealized.ToString("C2")
                      + ", fills " + _avgRec.Fills.Count);
            }

            _inTrade = false;
            _flattenPending = false;
            _stopChangePending = false;
            _lastStopSent = double.NaN;
            _stopCancelAt = DateTime.MinValue;

            // Cancel whatever is still resting, BEFORE the references are dropped.
            // Nothing in this strategy ever cancelled these, and every one was
            // submitted liveUntilCancelled: after the last take-profit filled, the
            // protective stop stayed working against a position that no longer
            // existed. Ordering matters — _inTrade is already false above, so the
            // "a stop cancelled while positioned is a human pulling it in Chart
            // Trader" branch in OnOrderUpdate cannot misread these, and CancelOrder
            // can deliver its update in-stack.
            CancelBracketLegs();

            _entryOrder = null;
            _stopOrder = null;
            _avgTpOrder = null;
            _avgArmed = false;
            _avgPlan = null;
            _avgRec = null;
            _avgQty = 0;
            _avgAvgPx = 0.0;
            _avgRealizedPts = 0.0;
            _avgTpChangePending = false;
            _avgLastTpSent = double.NaN;
            _avgTpLastQty = 0;
            for (int i = 0; i < BbExitConfig.MAX_TIERS; i++)
                _tierOrders[i] = null;

            _bracket.Dir = 0;
            _dir = 0;

            // Cleared here, not at the next entry: a stale reason on the next
            // trade's record is indistinguishable from a real one.
            _exitReason = "";

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

            // Realised P&L is accumulated here, ONCE per exit execution and
            // before any branch below decides what else to do about it — the
            // tier branch used to fall through to the close-out, so counting it
            // inside both would have double-booked the last tier.
            if (_inTrade && (sig == SigStop || sig == SigFlatten || sig == SigAvgTp || IsTierSig(sig)))
            {
                BbExits.AddExitFill(_bracket, price, quantity);
                if (_avgArmed && (sig == SigStop || sig == SigFlatten || sig == SigAvgTp))
                {
                    // The stack books its own realised points, against the LIVE
                    // average rather than P0. Adds fill at better prices than the
                    // entry, so BbExits' P0 basis overstates the loss on a stopped
                    // stack and understates the win on a TP — the same order of
                    // magnitude as G itself at the NQ defaults. BbExits is left
                    // alone on purpose: for the base 3-tier bracket the entry
                    // price IS the basis, and that path must not move.
                    _avgRealizedPts += (price - _avgAvgPx) * _dir * quantity;
                    // Level -2 = an exit execution. PlannedPx carries the average
                    // basis at that moment, which is what makes the competitor arm
                    // computable offline from the JSONL alone. (-1 is the entry.)
                    _avgRec.Fills.Add(new AvgFillRec
                    {
                        Ts = time, Level = -2,
                        PlannedPx = _avgAvgPx, FillPx = price, Qty = quantity
                    });
                    if (sig == SigAvgTp)
                    {
                        _bracket.QtyOpen = _avgQty - _bracket.QtyClosed;   // mirror of OnAddExecution: books stay truthful on the averaging exit

                        // A PARTIAL TP fill shrank the stack, and the tier
                        // branch that re-covers the base bracket never runs here
                        // (an averaging trade has Tiers == 0). Without this, NT8
                        // cancels the now-oversized stop on its own, that cancel
                        // ref-matches, and the deferred verdict latches
                        // StopCancelled on a stack that still has contracts in
                        // the market. Same idiom as the tier branch, and gated on
                        // QtyOpen so the fill that EMPTIED the position falls
                        // through to the close-out below instead.
                        if (_bracket.QtyOpen > 0)
                        {
                            _stopOrder = null;
                            _lastStopSent = double.NaN;
                            SubmitStop("avgtp:resize");
                        }
                    }
                }
            }

            // Entry fill. Gated on the signal NAME, not on a bool: by the time
            // this arrives, another submit may already have flipped the flag.
            if ((sig == SigLong || sig == SigShort) && execution.Order.OrderState == OrderState.Filled)
            {
                AdoptEntryFill(price, execution.Order.Filled, time);
                return;
            }

            // An add that fills AFTER the position went flat — the stop and the
            // add raced and the add landed second. Nothing downstream would catch
            // it: _avgArmed is already false, so the add branch below is dead and
            // the contracts would sit in the market with no stop behind them. The
            // direction comes off the order, never off _dir, which WentFlat zeroed.
            int _orphanLvl;
            if (TryAddSigLevel(sig, out _orphanLvl) && quantity > 0 && !_inTrade)
            {
                Print("BreakBox AVG: ORPHAN add fill after flat — exiting " + quantity + " at market");
                if (execution.Order.IsLong) ExitLong(0, quantity, SigOrphanExit, sig);
                else ExitShort(0, quantity, SigOrphanExit, sig);
                return;
            }

            // An add executed. Accumulate OUR average/quantity (Position may be
            // stale in-stack), recompute stop+TP from the REAL numbers, resize.
            int _addLvl;
            if (_avgArmed && _inTrade && TryAddSigLevel(sig, out _addLvl) && quantity > 0
                && (execution.Order.OrderState == OrderState.Filled
                    || execution.Order.OrderState == OrderState.PartFilled))
            {
                OnAddExecution(_addLvl, price, quantity, time);
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
                    //
                    // The ref is dropped FIRST because that auto-shrink cancels
                    // the old stop on its own initiative, at a moment we do not
                    // control — possibly before this line. Nulling here makes
                    // that Cancelled unmatchable too, so the hand-pull detector
                    // reads NT8's housekeeping for what it is. This is the bug
                    // that left a 1-lot runner unprotected after TP1.
                    _stopOrder = null;
                    _lastStopSent = double.NaN;
                    SubmitStop(d.StopMoved ? "tier:be" : "tier:resize");
                    DrawLevels();
                    return;
                }

                // The last tier emptied the position. Returning here — which is
                // what this did — skipped the close-out below, so `_inTrade`
                // stayed true for the rest of the session, the `in trade` rung
                // blocked BOTH engines, and the trade was never journalled. The
                // engine already announces this case as d.Why == "tier_flat";
                // the shell has to act on it instead of dropping it.
                break;
            }

            // Any exit that leaves us flat closes the trade out. `QtyOpen` is
            // checked FIRST and on its own: inside an execution event NT8 has not
            // necessarily updated Position yet ([[nt8-order-event-race]]), so a
            // condition resting only on MarketPosition loses the same race the
            // rest of this shell is written to survive. Tier fills decrement
            // QtyOpen; a stop or flatten fill does not, which is why the position
            // check stays as the other half of the OR.
            if (_inTrade && (_bracket.QtyOpen <= 0 || Position.MarketPosition == MarketPosition.Flat))
            {
                // The exit order's own signal name is the only honest reason
                // available here — BB_Stop / BB_TP2 / BB_Flatten. FlattenAll
                // already set a richer one, so it wins.
                if (_exitReason.Length == 0)
                    _exitReason = sig;
                WentFlat(price);
            }
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
                // Adopt anything EXCEPT an order on its way out. The submit
                // paths drop the reference on purpose before every
                // cancel-replace, and re-adopting the dead order here would put
                // it straight back — which both hands the hand-pull detector
                // below a false match and, when the replacement's Working event
                // happens to arrive first, leaves CancelBracketLegs holding a
                // corpse while the real stop rests on after the trade is over.
                //
                // The WHOLE cancel path is a corpse, not just the terminal
                // state: NT8 walks CancelPending -> CancelSubmitted ->
                // Cancelled, and adopting either of the first two puts the dead
                // order back in front of the detector. ChangePending/
                // ChangeSubmitted are NOT on this list — those are a modify of
                // the same live order, which is still ours.
                if (orderState != OrderState.Cancelled
                    && orderState != OrderState.CancelPending
                    && orderState != OrderState.CancelSubmitted)
                    _stopOrder = order;
                if (orderState == OrderState.Working || orderState == OrderState.Accepted)
                {
                    _stopChangePending = false;
                    // A working stop is proof the position is covered, so any
                    // pending cancel alarm was about an order we have replaced.
                    _stopCancelAt = DateTime.MinValue;
                }
            }
            else if (sig == SigAvgTp)
            {
                _avgTpOrder = order;
                if (orderState == OrderState.Working || orderState == OrderState.Accepted)
                    _avgTpChangePending = false;
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
                // A rejected ADD is survivable: fewer contracts is strictly
                // SAFER under the envelope (monotone loss). Mark the level dead
                // and keep trading — routing this through FlattenAll would turn
                // a routine rejection into a realized loss.
                int _rejLvl;
                if (TryAddSigLevel(sig, out _rejLvl))
                {
                    if (_avgPlan != null && _rejLvl >= 0 && _rejLvl < _avgPlan.Levels)
                        _avgPlan.Dead[_rejLvl] = true;
                    Print("BreakBox AVG: add level " + (_rejLvl + 1) + " dead after rejection — position keeps its current size");
                    return;
                }
                if (sig == SigStop || sig == SigTp1 || sig == SigTp2 || sig == SigTp3 || sig == SigAvgTp)
                    FlattenAll("leg_rejected");
                else if (sig == SigLong || sig == SigShort)
                {
                    // Same trap as CancelWorkingEntry: a rejection can land on an
                    // order that already traded part of its quantity.
                    if (order.Filled > 0 && !_inTrade)
                    {
                        AdoptEntryFill(averageFillPrice, filled, time);
                        return;
                    }
                    _entryPending = false;
                    if (_entryFromEngine)
                    {
                        _entryFromEngine = false;
                        OnEntryRejected(_owningEngine, "order_rejected");
                    }
                }
                return;
            }

            // A stop CANCELLED while we are still positioned, and not by us,
            // MIGHT be a human pulling it in Chart Trader. Only might: the
            // matching REFERENCE is what separates the candidates. Every cancel
            // this strategy causes — a resize, a breakeven move, NT8's own OCO
            // shrink when a tier fills — is preceded by dropping the reference,
            // so a Cancelled that still matches the order we believe is live is
            // the one nobody here asked for. Matching on the signal NAME
            // instead, which is what this did, could not tell the two apart,
            // and adopted "by hand" on the platform's own housekeeping.
            //
            // Nothing is decided here. This only raises the alarm; the verdict
            // is the deferred check in OnBarUpdate, which gives our machinery
            // the grace window to re-cover the position first.
            if (sig == SigStop && orderState == OrderState.Cancelled && _inTrade && !_flattenPending
                && _stopOrder != null && order == _stopOrder)
            {
                _stopCancelAt = DateTime.Now;
                _stopOrder = null;                  // that order is gone either way
            }
        }

        #endregion

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
                // §68 amendment: a file from a long-running instance can hold
                // more than the panel ever needs in RAM. Cap AFTER loading, not
                // by skipping early lines on the way in — dropping from the
                // front here is one call, and it keeps the load loop simple.
                if (_history.Count > BbHistory.MaxInMemory)
                {
                    BbHistory.TrimFront(_history, BbHistory.MaxInMemory);
                    _historyCapped = true;
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
            // §68 amendment: cap AFTER the file-write decision below, which
            // reads `r` — the record just appended — not `_history`. Trimming
            // the in-memory render cache must never change what gets written
            // to disk; the file is the durable record.
            if (_history.Count > BbHistory.MaxInMemory)
            {
                BbHistory.TrimFront(_history, BbHistory.MaxInMemory);
                _historyCapped = true;
            }

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
            EngineLog("locked out: " + why);
        }

        #endregion

        #region Drawing

        // Mirrors BreakBoxVision.PaintBox() (BreakBoxVision.cs:441) — see that
        // comment for the full reasoning. Three bugs this replaces:
        //   - Tag("box")/Tag("boxlab") mint a NEW tag every new box (the
        //     box.Id guard above only stops re-drawing the SAME box, not
        //     the accumulation of one permanent rectangle per box across
        //     the session). A stable tag keyed on box.Id means a redraw of
        //     that same id replaces the old drawing instead of stacking a
        //     new one — cheap insurance against a recalc re-running
        //     OnBarUpdate over history and re-emitting the same box ids.
        //   - Right edge was `Time[0].AddHours(4)` (480 bars on a 30s
        //     chart) instead of `Time[1]`, so every box's rectangle spanned
        //     nearly the whole visible window and they all overlapped.
        //   - Left edge was `box.SealedAt` (the sealing bar) instead of
        //     walking back the box's own BoxLookback — WindowRange() reads
        //     its ring BEFORE the sealing bar is pushed, so SealedAt was
        //     never part of the measured range.
        private void DrawBox()
        {
            var box = _engine.Box;
            if (box == null || box.Id == _lastDrawnBoxId)
                return;
            _lastDrawnBoxId = box.Id;

            int barsBack = Math.Min(_cfg.BoxLookback, CurrentBar);
            DateTime left = Time[barsBack];

            Brush b = box.Valid ? Brushes.DeepSkyBlue : Brushes.Gray;
            DrawTag(Draw.Rectangle(this, "bb_box_" + box.Id, false, left, box.Low, Time[1],
                                   box.High, b, b, 12));
            DrawTag(Draw.Text(this, "bb_boxlab_" + box.Id, box.Valid ? "BOX" : "BOX (out of band)", 0,
                              box.High + 4 * TickSize, b));
        }

        private void DrawLevels()
        {
            if (!ShowLevels || !_inTrade)
                return;
            DrawTag(Draw.HorizontalLine(this, "BB_stop", _bracket.StopPx, Brushes.Red));

            if (_avgArmed && _avgPlan != null)
            {
                // The averaging grid: unfired levels goldenrod, fired gray,
                // dead (aborted before touch) dark red — plus the single
                // dynamic TP. No tiers to draw; _bracket.Tiers is 0 here.
                for (int i = 0; i < _avgPlan.Levels; i++)
                {
                    Brush b = _avgPlan.Fired[i] ? Brushes.Gray
                            : _avgPlan.Dead[i] ? Brushes.DarkRed : Brushes.Goldenrod;
                    DrawTag(Draw.HorizontalLine(this, "BB_avgL" + i, _avgPlan.LevelPx[i], b));
                }
                if (!double.IsNaN(_avgLastTpSent))
                    DrawTag(Draw.HorizontalLine(this, "BB_avgTp", _avgLastTpSent, Brushes.LimeGreen));
            }
            else
            {
                for (int i = 0; i < _bracket.Tiers; i++)
                {
                    if (_bracket.TierFilled[i])
                        continue;
                    DrawTag(Draw.HorizontalLine(this, "BB_tp" + (i + 1), _bracket.TargetPx[i], Brushes.LimeGreen));
                }
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

        [NinjaScriptProperty]
        [Display(Name = "Enable Break engine", Order = 1, GroupName = "03. Engines")]
        public bool EnableBreak { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow long", Order = 3, GroupName = "03. Engines")]
        public bool AllowLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow short", Order = 4, GroupName = "03. Engines")]
        public bool AllowShort { get; set; }

        [NinjaScriptProperty, Range(5, 3600)]
        [Display(Name = "Trigger life (seconds)", Order = 7, GroupName = "03. Engines")]
        public int TriggerLifeSec { get; set; }

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

        [NinjaScriptProperty]
        [Display(Name = "Cloud: break the pullback base", Description = "G — true: a STOP beyond the ceiling of the basing action at the bottom of the pullback, armed before the impulse bar. false: a STOP beyond the reclaim bar's own high, which a tall bar carries with it. In the config digest, so the cfg view separates the two curves", Order = 26, GroupName = "03. Engines")]
        public bool CloudPullbackBreak { get; set; }

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
        [Display(Name = "Averaging enabled (SIM-ONLY LAB)", Description = "Averaging-down laboratory. Refuses to arm on any non-Sim/Playback account. Module-ON is a DIFFERENT strategy from module-OFF and validates separately.", Order = 1, GroupName = "08. Averaging lab")]
        public bool AveragingEnabled { get; set; }

        [NinjaScriptProperty, Range(1, 32)]
        [Display(Name = "Max adds (N)", Description = "User-defined depth; the budget still binds — a deep grid solves to a tighter spacing or refuses to arm", Order = 2, GroupName = "08. Averaging lab")]
        public int AveragingMaxAdds { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Add quantity (q)", Order = 3, GroupName = "08. Averaging lab")]
        public int AveragingAddQty { get; set; }

        [NinjaScriptProperty, Range(0.0, 1000000.0)]
        [Display(Name = "Budget per trade ($, 0 = use fraction)", Description = "Direct dollar budget for one averaging trade. 0 derives it as fraction x daily loss limit. Either way it is capped by what is LEFT of today's daily loss limit — one trade may never out-risk the day.", Order = 4, GroupName = "08. Averaging lab")]
        public double AveragingBudgetDollars { get; set; }

        [NinjaScriptProperty, Range(0.05, 1.0)]
        [Display(Name = "Budget fraction of daily loss", Description = "One trade's slice of DailyLossLimit; used only when Budget ($) = 0. Also capped by what is left of the day", Order = 5, GroupName = "08. Averaging lab")]
        public double AveragingBudgetFraction { get; set; }

        [NinjaScriptProperty, Range(1.0, 100000.0)]
        [Display(Name = "Target profit G ($, net)", Description = "The trade still exits at this net dollar profit. Floor: G >= stack * (8 ticks * tickValue - commission)", Order = 6, GroupName = "08. Averaging lab")]
        public double AveragingTargetProfitDollars { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Stop buffer s (ticks)", Description = "Below the deepest level; raised to d/2 at arm if smaller", Order = 7, GroupName = "08. Averaging lab")]
        public int AveragingStopBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Spacing source", Description = "Auto = box height when the box engine owns the trade, ATR otherwise; the budget-solved d caps it either way", Order = 8, GroupName = "08. Averaging lab")]
        public AvgSpacingSource AveragingSpacingSource { get; set; }

        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "Spacing ATR mult", Description = "Structural spacing when the source resolves to ATR", Order = 9, GroupName = "08. Averaging lab")]
        public double AveragingSpacingAtrMult { get; set; }

        [NinjaScriptProperty, Range(1, 5)]
        [Display(Name = "Confirm bars", Description = "Bar closes back beyond a touched level before adding — straight-line moves never confirm", Order = 10, GroupName = "08. Averaging lab")]
        public int AveragingConfirmBars { get; set; }

        [NinjaScriptProperty, Range(1.0, 10.0)]
        [Display(Name = "Vol abort mult", Description = "One-way: ATR above this multiple of the entry ATR kills the remaining adds", Order = 11, GroupName = "08. Averaging lab")]
        public double AveragingVolAbortMult { get; set; }

        [NinjaScriptProperty, Range(0, 120)]
        [Display(Name = "No adds final minutes", Description = "No arming or adding this close to FlattenHhmm", Order = 12, GroupName = "08. Averaging lab")]
        public int AveragingNoAddsFinalMinutes { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "Commission RT ($/contract)", Order = 13, GroupName = "08. Averaging lab")]
        public double AveragingCommissionRt { get; set; }

        [NinjaScriptProperty, Range(0, 40)]
        [Display(Name = "Slippage reserve (ticks)", Description = "Reserved out of the budget for the full stack's stop", Order = 14, GroupName = "08. Averaging lab")]
        public int AveragingSlippageReserveTicks { get; set; }

        #endregion
    }
}
