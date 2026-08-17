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
