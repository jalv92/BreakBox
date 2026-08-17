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
                // A long token in a short regime would fire the wrong way with an
                // ext that is a low. Kill BEFORE the latch is overwritten, while
                // the old direction is still readable.
                if (_st.RegimeLatched != 0 && now != _st.RegimeLatched)
                    KillToken("regime flipped");
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
