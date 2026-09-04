// TrendStCore.cs — the TrendST setup detector. Pure: ZERO `using NinjaTrader.*`,
// namespace BreakBoxCore, C# 7.3 — it compiles inside NT8's Custom assembly AND
// in the net8 test runner (tests/TrendStTests.cs), same rule as the other
// BreakBox core files.
//
// The setup, long side (short is the mirror):
//   1. The cloud regime is UP (the caller reads BbCloudState.RegimeLatched —
//      the same latched regime BreakBoxVision paints green).
//   2. A pullback: >= MinPullbackBars consecutive closed bars whose LOW touches
//      or enters the ribbon (Low <= fast EMA). The bar just before the first
//      pullback bar is the "tip" (the impulse top).
//   3. Volume decays from the tip to the end of the pullback: the LAST pullback
//      bar's volume is below DecayRatio x the TIP's volume. Endpoints only, the
//      line from the tip to the end — one big bar in the middle must not kill
//      the setup.
//   4. The signal bar: bullish body (Close > Open) that closes ABOVE the
//      previous bar's high (the engulfing). Entry is at its close.
// A bar that neither touches the ribbon nor is the signal ends the pullback;
// a regime change resets everything.
using System;

namespace BreakBoxCore
{
    public sealed class TrendStConfig
    {
        public int MinPullbackBars = 2;
        public int MaxPullbackBars = 10;        // ponytail: a ride inside the ribbon this long is a range, not a pullback
        public double DecayRatio = 0.8;         // last pullback bar volume < ratio x tip volume
    }

    public sealed class TrendStSetup
    {
        private readonly TrendStConfig _cfg;
        private readonly double[] _vols;        // [0] = tip, [1..n] = pullback bars
        private int _n;                         // pullback bars so far (0 = no pullback open)
        private int _dir;                       // regime the open pullback belongs to
        private bool _havePrev;
        private BbBar _prev;

        public string Why = "none";             // last decision, for the Output window

        public TrendStSetup(TrendStConfig cfg)
        {
            _cfg = cfg;
            _vols = new double[(_cfg.MaxPullbackBars < 1 ? 1 : _cfg.MaxPullbackBars) + 1];
        }

        public void Reset()
        {
            _n = 0;
            _dir = 0;
            _havePrev = false;
        }

        // One CLOSED bar. `regime` is the latched cloud regime (+1/-1/0), `eF`
        // the fast ribbon EMA already updated with this bar. Returns +1/-1 on
        // the signal bar, else 0.
        public int OnBar(BbBar bar, int regime, double eF)
        {
            int fire = 0;
            BbBar prev = _prev;
            bool havePrev = _havePrev;
            _prev = bar;
            _havePrev = true;

            if (regime == 0 || regime != _dir)
            {
                _n = 0;                         // regime changed: any open pullback is void
                _dir = regime;
                Why = regime == 0 ? "no regime" : "regime " + (regime > 0 ? "up" : "down") + ", waiting for a pullback";
                if (regime == 0)
                    return 0;
            }

            bool touches = regime > 0 ? bar.Low <= eF : bar.High >= eF;
            bool body = regime > 0 ? bar.Close > bar.Open : bar.Close < bar.Open;
            bool engulf = havePrev && (regime > 0 ? bar.Close > prev.High : bar.Close < prev.Low);

            // Signal test FIRST: the engulfing bar may itself still touch the
            // ribbon (its low usually does), and it must not be swallowed as
            // pullback bar n+1.
            if (_n >= _cfg.MinPullbackBars && body && engulf)
            {
                if (VolumeDecayed())
                {
                    fire = regime;
                    Why = "signal: " + _n + "-bar pullback, volume decayed, engulfing close";
                }
                else
                    Why = "engulfing close but volume did not decay";
                _n = 0;
                return fire;
            }

            if (touches)
            {
                if (_n == 0)
                {
                    if (!havePrev)
                        return 0;
                    _vols[0] = prev.Volume;     // the tip
                }
                if (_n >= _cfg.MaxPullbackBars)
                {
                    _n = 0;
                    Why = "pullback too long, reset";
                    return 0;
                }
                _n++;
                _vols[_n] = bar.Volume;
                Why = "pullback bar " + _n;
                return 0;
            }

            if (_n > 0)
                Why = "left the ribbon without a signal, reset";
            _n = 0;
            return 0;
        }

        private bool VolumeDecayed()
        {
            return _vols[0] > 0 && _vols[_n] < _cfg.DecayRatio * _vols[0];
        }
    }
}
