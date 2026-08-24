// BreakBoxTypes.cs — the primitives every pure engine in BreakBox shares: bars,
// swings, the house ATR, the house pivot detector, a hand-rolled EMA, and the
// arithmetic helpers.
//
// ZERO `using NinjaTrader.*`, own namespace `BreakBoxCore`, C# 7.3 only. NT8
// ships its own types under common names and compiles every file under
// bin/Custom into ONE assembly, so a NinjaTrader using here is how a CS0101
// duplicate-type clash starts. Purity is also what lets this exact file compile
// both inside that assembly and in the net8 test runner (tests/), which has no
// NinjaTrader assemblies on its reference path — the behaviour has to be
// identical in both.
//
// WilderAtr, SwingDetector and BbMath are VERBATIM ports of VeeSnapTypes.cs
// (itself ported from PatternZoneCore.cs). The ports are verbatim on purpose:
// the ATR recursion and the pivot reveal rule are pinned by two other test
// suites and mirrored in Python, so "improving" either of them here silently
// forks three implementations. Only the namespace and the type prefix change.
using System;
using System.Collections.Generic;

namespace BreakBoxCore
{
    public struct BbBar
    {
        public DateTime Time;
        public double Open, High, Low, Close, Volume;
    }

    public struct BbSwing
    {
        public int BarIndex;
        public DateTime Time;
        public double Price;
        public bool IsHigh;
    }

    // House Wilder recursion (VeeSnapTypes.cs:41-71): seed = tr[0] itself (a
    // running mean of one sample), TR uses the previous bar's close, and it
    // CROSSES SESSIONS — it never resets. The first ~period bars of a session
    // therefore carry the overnight gap in their true range.
    public sealed class WilderAtr
    {
        private readonly int _period;
        private int _n;
        private double _value;
        private double _prevClose;

        public WilderAtr(int period)
        {
            _period = period;
        }

        public void Update(BbBar bar)
        {
            double tr = _n == 0
                ? bar.High - bar.Low
                : Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - _prevClose), Math.Abs(bar.Low - _prevClose)));
            _value = _n < _period ? (_value * _n + tr) / (_n + 1) : _value + (tr - _value) / _period;
            _prevClose = bar.Close;
            _n++;
        }

        public double Value { get { return _value; } }

        // Warm, not merely non-zero: a partially warmed ATR is positive and
        // shrinks every ATR-scaled gate proportionally, which reads as "the
        // strategy took a trade it should not have" rather than as a warmup bug.
        public bool IsWarm { get { return _n >= _period && _value > 0; } }

        public int BarsFed { get { return _n; } }
    }

    // Hand-rolled EMA. Exists because two of the five stop sources the target's
    // panel exposes are moving averages (`MA` and `E50`), and nt8c cannot
    // resolve NT8's EMA() wrapper from a pure file (workspace gotcha) — but more
    // importantly because the stop price has to be reproducible in the test
    // runner, which has no NT8 assemblies at all.
    //
    // Seeded with the first sample rather than with a simple average of the
    // first `period` samples: an SMA seed is a second recursion to reproduce and
    // buys nothing once IsWarm gates the consumers anyway.
    public sealed class Ema
    {
        private readonly int _period;
        private readonly double _alpha;
        private int _n;
        private double _value;

        public Ema(int period)
        {
            _period = period < 1 ? 1 : period;
            _alpha = 2.0 / (_period + 1.0);
        }

        public void Update(double sample)
        {
            // Written as a*x + (1-a)*prev, NOT prev + a*(x-prev): the two forms
            // differ in the last bits and only this one is exactly `x` at a = 1.
            _value = _n == 0 ? sample : _alpha * sample + (1.0 - _alpha) * _value;
            _n++;
        }

        public double Value { get { return _value; } }
        public bool IsWarm { get { return _n >= _period; } }
        public int BarsFed { get { return _n; } }
    }

    // House reveal rule (VeeSnapTypes.cs:78-124): the pivot sits `strength` bars
    // back and is confirmed once its window fills; strict-unique max/min over the
    // 2*strength+1 window — an equal extreme anywhere else in the window rejects
    // it. One bar can confirm a high AND a low at once.
    public sealed class SwingDetector
    {
        private readonly int _strength;
        private readonly int _windowSize;
        private readonly List<BbBar> _window = new List<BbBar>();

        public SwingDetector(int strength)
        {
            _strength = strength;
            _windowSize = 2 * strength + 1;
        }

        public List<BbSwing> Update(BbBar bar, int barIndex)
        {
            _window.Add(bar);
            if (_window.Count > _windowSize)
                _window.RemoveAt(0);

            var result = new List<BbSwing>();
            if (_window.Count < _windowSize)
                return result;

            BbBar candidate = _window[_strength];
            int candidateIndex = barIndex - _strength;
            double ph = candidate.High, pl = candidate.Low;
            bool hiMax = true, loMin = true;
            int hiEq = 0, loEq = 0;
            for (int i = 0; i < _window.Count; i++)
            {
                BbBar w = _window[i];
                if (w.High > ph) hiMax = false;
                else if (w.High == ph) hiEq++;
                if (w.Low < pl) loMin = false;
                else if (w.Low == pl) loEq++;
            }
            if (hiMax && hiEq == 1)
                result.Add(new BbSwing { BarIndex = candidateIndex, Time = candidate.Time, Price = ph, IsHigh = true });
            if (loMin && loEq == 1)
                result.Add(new BbSwing { BarIndex = candidateIndex, Time = candidate.Time, Price = pl, IsHigh = false });
            return result;
        }

        public void Reset()
        {
            _window.Clear();
        }
    }

    public static class BbMath
    {
        // True when a STOP at triggerPx sits on the side of the market the
        // exchange will not accept: a BUY stop at or below the offer, a SELL
        // stop at or above the bid. NT8 rejects those with a modal error that
        // also stalls Playback, and the sign is exactly the kind of thing that
        // reads correct while being backwards — so it lives here, with asserts
        // on it, instead of inline in the order path.
        //
        // sidePx is the side that would TRIGGER the stop: the ask for a buy
        // stop, the bid for a sell stop. An unavailable quote (NaN or <= 0)
        // answers false: refusing every entry because the feed went quiet is a
        // worse failure than letting the platform arbitrate one order.
        public static bool StopThroughMarket(double triggerPx, double sidePx, int dir)
        {
            if (dir == 0 || double.IsNaN(sidePx) || double.IsInfinity(sidePx) || sidePx <= 0.0)
                return false;
            return (triggerPx - sidePx) * dir <= 0.0;
        }

        // The daily governor's verdict on a day P&L, as a lockout reason or "".
        // It lives here, with asserts on it, because the two comparisons are
        // sign-sensitive and a backwards one reads perfectly correct: the loss
        // limit is a POSITIVE dollar figure that has to be compared against a
        // NEGATIVE P&L, and the target a positive one against a positive P&L.
        //
        // dayPnl must already include the open position. A governor fed realized
        // P&L only cannot see a losing runner at all — that was the v2 bug.
        public static string DayGovernor(double dayPnl, double lossLimit, double profitTarget)
        {
            if (double.IsNaN(dayPnl))
                return "";
            if (lossLimit > 0.0 && dayPnl <= -lossLimit)
                return "daily_loss";
            if (profitTarget > 0.0 && dayPnl >= profitTarget)
                return "daily_target";
            return "";
        }

        // Hand-rolled so every engine rounds identically, and so the test runner
        // rounds the way NT8 will. Instrument.MasterInstrument.RoundToTickSize
        // must NEVER touch a price computed here.
        public static double RoundToTick(double px, double tick)
        {
            if (tick <= 0)
                return px;
            return Math.Floor(px / tick + 0.5) * tick;
        }

        public static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public static double Lerp(double a, double b, double u)
        {
            return a + (b - a) * u;
        }

        // Maps a score in [lo, hi] onto [0, 1], clamped. `hi <= lo` collapses to
        // 0 rather than dividing by zero — a degenerate configuration must
        // behave like "never aggressive", not like NaN, because every comparison
        // against NaN is false and the guards would all fail open.
        public static double Unit(double v, double lo, double hi)
        {
            if (hi <= lo)
                return 0.0;
            return Clamp((v - lo) / (hi - lo), 0.0, 1.0);
        }

        // Where in its own range did this bar CLOSE, seen from `dir`? 1.00 is a
        // wickless close on the extreme — which is what BOTH reference signal
        // candles read (spec §2) — and 0.00 closes on the wrong end.
        //
        // NaN on a zero-range bar, deliberately. Every comparison against NaN is
        // false, so a flat bar FAILS the gate instead of passing it on a 0/0;
        // the alternative is that a halted tape prints the best-looking signal
        // candle of the session.
        public static double CloseInRange(BbBar bar, int dir)
        {
            double range = bar.High - bar.Low;
            if (range <= 0.0)
                return double.NaN;
            return dir > 0 ? (bar.Close - bar.Low) / range : (bar.High - bar.Close) / range;
        }

        // ET seconds-of-day from an HHMM integer. 930 -> 34200. Used by every
        // window parameter on the panel and in the strategy.
        public static int HhmmToSecs(int hhmm)
        {
            int h = hhmm / 100;
            int m = hhmm % 100;
            return h * 3600 + m * 60;
        }

        // Is `secs` inside [start, end) on a clock that wraps at midnight? The
        // ETH session opens at 18:00 and closes at 17:00 the next day, so EVERY
        // window in this strategy can wrap and none of them may be compared with
        // a naive start <= x < end.
        public static bool InWindow(int secs, int startSecs, int endSecs)
        {
            if (startSecs == endSecs)
                return false;                   // an empty window is empty, not "always"
            return startSecs < endSecs
                ? secs >= startSecs && secs < endSecs
                : secs >= startSecs || secs < endSecs;
        }
    }

    // What blocked an engine on THIS bar, in the words the panel prints.
    //
    // One instance per engine and never shared (§4.2): two engines writing one
    // report puts the cloud's ladder under the box's headline, which is a worse
    // failure than no ladder at all — it reads as an explanation.
    public sealed class BbGateReport
    {
        public string Block = "";           // "" when nothing blocks
        public string BlockDetail = "";     // "range 275.00 = 7.2x ATR (max 6.0)"
        public int GateDepth = -1;          // index of the first failing gate; -1 = none

        public void Set(string block, string detail, int depth)
        {
            // Empty, never null: this is written from early returns on the hot
            // path and read on the WPF thread, where a null surfaces as a
            // NullReferenceException one layer away from what caused it.
            Block = block ?? "";
            BlockDetail = detail ?? "";
            GateDepth = depth;
        }

        public void Clear()
        {
            Block = "";
            BlockDetail = "";
            GateDepth = -1;
        }

        // How the panel should render ladder row `row` given `depth`, the index
        // of the first failing gate (-1 = nothing blocks). 0 = passed,
        // 1 = the blocker, 2 = never evaluated.
        //
        // Pure and here rather than in the panel because "everything after the
        // blocker is DIMMED, not FAILED" is the entire point of the ladder: the
        // engine short-circuits at the first failure, so the rows below it were
        // never computed and any verdict on them is invented.
        public static int RowState(int row, int depth)
        {
            if (depth < 0)
                return 0;
            return row < depth ? 0 : (row == depth ? 1 : 2);
        }
    }

    // Seconds -> bars, and the bar-size estimate for non-time series. It lives
    // in Types because BOTH the strategy and its config builder need it and
    // neither may own it: the §8 contract is that no horizon is ever expressed
    // in bars on the parameter surface, so every dial passes through here on its
    // way in. v1 died of the opposite arrangement — a 240-MINUTE box range
    // divided by a 14-BAR ATR — and no tuning fixes a dimensional error.
    public static class BbScale
    {
        // What a non-time series falls back to when there is too little history
        // to estimate anything. 30s is the bar size the whole model was measured
        // on, so a wrong fallback is at least the right wrong number.
        public const int FallbackSeconds = 30;
        public const int MinEstimateSamples = 200;

        // A horizon in seconds, on a `barSec` series, floored at `min` bars.
        public static int Bars(int horizonSecs, int barSec, int min)
        {
            if (min < 1)
                min = 1;
            if (barSec < 1 || horizonSecs < 1)
                return min;
            int n = horizonSecs / barSec;
            return n < min ? min : n;
        }

        // Median seconds per bar over `count` consecutive inter-bar gaps.
        // Returns 0 for "cannot answer" — the caller falls back to
        // FallbackSeconds and prints that it did, because an approximate bar
        // size that announces itself is fine and one that does not is a lie the
        // whole parameter surface is built on.
        public static int EstimateBarSeconds(double[] gapSecs, int count)
        {
            if (gapSecs == null || count < MinEstimateSamples || count > gapSecs.Length)
                return 0;

            // Copy before sorting: the caller's buffer is its own history and
            // must survive being measured.
            double[] s = new double[count];
            Array.Copy(gapSecs, s, count);
            Array.Sort(s);

            double m = (count & 1) == 1
                ? s[count / 2]
                : 0.5 * (s[count / 2 - 1] + s[count / 2]);

            int secs = (int)Math.Floor(m + 0.5);
            return secs < 1 ? 1 : secs;
        }
    }

    // Account-wide daily governor — the shared ledger that lets N BreakBox
    // instances running on DIFFERENT instruments stop TOGETHER when their
    // COMBINED day P&L reaches the profit target (or the loss limit). Static,
    // so it is shared by every instance of the strategy inside the one NT8
    // process; keyed by account, so two accounts never mix.
    //
    // Each instance publishes ITS OWN day P&L and reads back the sum. It is
    // deliberately NOT Account.Get(Realized) + Get(Unrealized): those are two
    // separately-updated aggregates, and the instant a winner's target fills
    // realized is already credited while account unrealized still carries the
    // just-closed position — the sum double-counts that trade and fires the
    // target early (seen live on LatigoBreak 2026-08-10: a $750 target
    // flattened everything at $539 realized). One instance's own CumProfit +
    // Position pair is event-ordered on its own strategy thread, so it is an
    // internally consistent snapshot, and a sum of consistent numbers inherits
    // that.
    public static class BbAcctGov
    {
        private sealed class Entry
        {
            public DateTime Day;
            public bool Breached;
            public readonly Dictionary<string, double> PnL = new Dictionary<string, double>();
        }

        private static readonly object Lk = new object();
        private static readonly Dictionary<string, Entry> Accounts = new Dictionary<string, Entry>();

        // Publish this instance's day P&L, read back the account-wide sum.
        // false = shared mode cannot operate for this instance right now (no
        // account/day, or another instance already registered a NEWER trading
        // day — a lagging instance must not drag today's sum backwards with
        // yesterday's number). The caller then judges its own P&L alone.
        public static bool Publish(string account, DateTime day, string key, double pnl,
                                   out double sum, out bool breached)
        {
            sum = pnl;
            breached = false;
            if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(key) || day == DateTime.MinValue)
                return false;

            lock (Lk)
            {
                Entry e;
                Accounts.TryGetValue(account, out e);
                if (e != null && e.Day > day)
                    return false;
                if (e == null || e.Day < day)
                {
                    e = new Entry();
                    e.Day = day;
                    Accounts[account] = e;
                }
                e.PnL[key] = pnl;
                double t = 0.0;
                foreach (double v in e.PnL.Values)
                    t += v;
                sum = t;
                breached = e.Breached;
                return true;
            }
        }

        // Latch the breach for every other instance on this account and return
        // the per-instance breakdown for the log. Broadcasting through the
        // ledger — instead of letting each instance rediscover the sum on its
        // own next bar — is what makes them all flatten on the SAME bar, and it
        // keeps an instance whose own limits are 0 (off) locked out too.
        public static string Breach(string account, DateTime day)
        {
            if (string.IsNullOrEmpty(account))
                return "";
            lock (Lk)
            {
                Entry e;
                if (!Accounts.TryGetValue(account, out e) || e.Day != day)
                    return "";
                e.Breached = true;
                var sb = new System.Text.StringBuilder("[");
                foreach (KeyValuePair<string, double> kv in e.PnL)
                    sb.Append(kv.Key).Append(' ')
                      .Append(kv.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                      .Append("; ");
                return sb.Append(']').ToString();
            }
        }

        // Playback rewind / a fresh strategy load: the discarded pass's numbers
        // and its breach latch must not survive into the new one. Instances
        // still running republish on their next bar, so the sum is whole again
        // within one bar.
        public static void Reset(string account)
        {
            if (string.IsNullOrEmpty(account))
                return;
            lock (Lk)
                Accounts.Remove(account);
        }
    }
}
