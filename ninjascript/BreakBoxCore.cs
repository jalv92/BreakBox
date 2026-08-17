// BreakBoxCore.cs — Engine B, the box. Pure decision code: it never touches an
// order, never reads a clock, never does I/O.
//
// ZERO `using NinjaTrader.*`, own namespace `BreakBoxCore`, C# 7.3 only — same
// rules and same reason as BreakBoxTypes.cs.
//
// WHY v1's BOX IS DELETED RATHER THAN RETUNED. v1 built a 240-MINUTE wall-clock
// range and validated it against a 14-BAR ATR: 275 points / 7.85 = 35 ATR
// against a ceiling of 6. No box was ever valid, so no engine ever ran and the
// strategy took ZERO trades. That is a dimensional error, not a tuning problem.
// The whole slot machinery is therefore gone — SlotOf, BbBoxSource, HtfMinutes,
// IbStartHhmm, IbMinutes, SessionCloseHhmm — and with it BreakSpentDir. Deleting
// them CLOSES B11 (the SlotOf off-by-one) and B12 (BreakSpentDir, a single int
// doing the work of a per-direction latch): the code that held both bugs no
// longer exists. The Retrace engine goes too — it is retired, and only its enum
// value survives so saved workspaces do not shift.
//
// WHAT A BOX IS IN v2. A micro accumulation measured in BARS all the way down:
// the range of the last BoxLookback CLOSED bars, judged against a percentile of
// that same measurement over the recent past, then validated against the mean
// range of boxes sealed before it. Every number in the chain is a bar range over
// a bar range — dimensionless, and it survives a change of bar size, which is
// exactly what MinBoxRangeAtr / MaxBoxRangeAtr were failing to express.
//
// TIMING. Every decision is taken at a BAR CLOSE, from closed bars only, and the
// formation window EXCLUDES the bar being processed: including it is a one-bar
// lookahead that lets the range see the break it is about to be tested against.
using System;
using System.Globalization;

namespace BreakBoxCore
{
    public enum BbEntryEngine
    {
        Break = 0,
        Retrace = 1,            // retired in v2; the value is retained so saved workspaces do not shift
        Cloud = 2
    }

    // The five stop sources the target's panel exposes. Priced in BreakBoxExits;
    // named here because the core reports the signal candle that `Candle` needs.
    public enum BbStopSource
    {
        Candle = 0,
        Swing = 1,
        Ma = 2,
        Ema50 = 3,
        Manual = 4
    }

    public sealed class BbBox
    {
        public double High;
        public double Low;
        public DateTime SealedAt;       // the bar that froze the edges
        public bool Valid;              // passed the §6.1 validity gate at seal time
        public int Id;                  // monotone; arming is counted per edge PER ID

        public double Range { get { return High - Low; } }
    }

    public sealed class BbConfig
    {
        public double TickSize = 0.25;

        // --- Box lifecycle (§6.1). Every horizon here is in BARS and is
        // converted from a SECONDS parameter inside the shell's BuildConfigs().
        // BoxMinBars is the exception: it is a confirmation COUNT, not a
        // horizon, so it does not scale with bar size.
        public int BoxLookback = 7;
        public int BoxMinBars = 2;
        public double BoxRangePctile = 35.0;
        public int BoxSampleN = 200;
        public int BoxMeanSamples = 20;
        public double BoxValidLo = 0.4;
        public double BoxValidHi = 2.5;
        public double BoxDeadAtr = 0.5;
        public int BoxMaxAge = 60;
        public int BoxArmsPerEdge = 2;
        public int BoxArmCooldown = 6;

        // --- Entry. Same stop-market mechanism as the cloud engine's §5.2
        // step 6, and the SAME offset dial: two dials meaning "how far beyond
        // the bar" is one dial and one place to disagree with yourself.
        public int TriggerOffsetTicks = 1;
        public int TriggerLife = 4;

        // --- Gating
        public bool EnableBreak = true;
        public bool AllowLong = true;
        public bool AllowShort = true;
        public int MaxTradesPerBox = 1;
        // A governor of last resort, not a plan. The design frequency is 8-12
        // fills per session (§13 step 4); the daily budget only has to stop a
        // runaway loop. DailyLossLimit in the shell is the real governor,
        // because it measures HOW MUCH, not HOW MANY. Phase 1 set this to 30
        // (B9) and the rewrite carries it over verbatim — a rewrite that
        // quietly restores an old default is how a closed bug reopens.
        public int MaxTradesPerDay = 30;
        public int EntryWindowStartHhmm = 930;
        public int EntryWindowEndHhmm = 1545;
        public int AtrPeriod = 14;
    }

    public struct BbAction
    {
        public bool Fire;
        public int Dir;                 // +1 long, -1 short
        public BbEntryEngine Engine;
        public double TriggerPx;        // stop price
        public bool IsLimit;            // false => stop-market
        public double SignalBarHigh;    // the `Candle` stop source reads these two
        public double SignalBarLow;
        public double BoxHigh, BoxLow;  // 0 when Engine == Cloud
        public int BoxId;
        public string Why;
    }

    // The engine's ONLY memory. Nothing it needs may live in the shell.
    public sealed class BbEngineState
    {
        // The engine's own gate report. Never shared with the cloud engine's:
        // two engines writing one report is how a panel ends up describing the
        // wrong ladder (§4.2).
        public readonly BbGateReport Gate = new BbGateReport();

        // Formation window: the last BoxLookback CLOSED bars, EXCLUDING the bar
        // being processed. Sized from the config, never from a constant.
        public double[] WinHigh, WinLow;
        public int WinIdx, WinFilled;

        // Every bar's window range, sampled UNCONDITIONALLY (§6.1). Gating the
        // sample on the formation test feeds the percentile only ranges that
        // already passed it — a loop that tightens forever until nothing forms.
        public double[] Samples;
        public int SampleIdx, SampleFilled;

        // The candidate under formation
        public bool CandOpen;
        public int CandBars;
        public double CandHigh, CandLow;

        // The sealed box and its age, in bars
        public BbBox Box;
        public int NextBoxId = 1;
        public int BoxAge;

        // Ranges of the last BoxMeanSamples SEALED boxes — the validity
        // denominator.
        public double[] SealedRanges;
        public int SealedIdx, SealedFilled;
        public int SealedCount;                 // lifetime count; the cold start reads this

        // Arming, per edge, per box Id (§6.2)
        public int ArmsUp, ArmsDn;
        public int LastArmBar = int.MinValue / 2;
        public int BarCount;

        public bool Armed;
        public int ArmDir;
        public double ArmTriggerPx;
        public int TriggerArmedBars;
        // Why the last disarm happened — the arm/expire/reject machinery lands
        // in T44+, but the shell's §4.1 arbitration (AgeWorkingEntry,
        // OnEntryRejected routing) already reads this every bar and must keep
        // compiling and logging a reason across the rewrite.
        public string DisarmReason = "";

        // Counters
        public int TradesThisBox;
        public int TradesToday;
        public DateTime CountedDay = DateTime.MinValue;
    }

    public sealed class BbEngine
    {
        // The gate ladder, in the order the panel renders it:
        //   0 atr warm · 1 box warming (cold start) · 2 box · 3 box valid
        //   4 auto-trade · 5 window · 6 budget · 7 armed · 8 break · 9 arms
        private readonly BbConfig _cfg;
        private readonly BbEngineState _st;

        public BbEngine(BbConfig cfg, BbEngineState st)
        {
            _cfg = cfg;
            _st = st;
            EnsureBuffers();
        }

        public BbBox Box { get { return _st.Box; } }
        public BbEngineState State { get { return _st; } }

        // The shell's §4.1 arbitration (AgeWorkingEntry) reads this every bar a
        // trigger is working to decide whether the resting order it produced is
        // still wanted. Arming itself lands in T44+; until then this simply
        // never goes true, which is the correct "nothing armed" answer.
        public bool BreakArmed { get { return _st.Armed; } }
        public string LastDisarmReason { get { return _st.DisarmReason; } }

        // Allocate only when the size actually changed. BuildConfigs() rebuilds
        // the engine on EVERY panel toggle, and blowing the sample ring away on
        // a toggle would restart the cold start from zero and mute the engine
        // for another BoxMeanSamples boxes — a dead strategy caused by clicking
        // a button.
        private void EnsureBuffers()
        {
            int look = _cfg.BoxLookback < 1 ? 1 : _cfg.BoxLookback;
            if (_st.WinHigh == null || _st.WinHigh.Length != look)
            {
                _st.WinHigh = new double[look];
                _st.WinLow = new double[look];
                _st.WinIdx = 0;
                _st.WinFilled = 0;
            }

            int n = _cfg.BoxSampleN < 2 ? 2 : _cfg.BoxSampleN;
            if (_st.Samples == null || _st.Samples.Length != n)
            {
                _st.Samples = new double[n];
                _st.SampleIdx = 0;
                _st.SampleFilled = 0;
            }

            int m = _cfg.BoxMeanSamples < 1 ? 1 : _cfg.BoxMeanSamples;
            if (_st.SealedRanges == null || _st.SealedRanges.Length != m)
            {
                _st.SealedRanges = new double[m];
                _st.SealedIdx = 0;
                _st.SealedFilled = 0;
            }
        }

        // Feed one CLOSED bar. `secs` is its ET seconds-of-day, `sessionDate` the
        // trading day it belongs to, `canTrade` whether the shell would accept an
        // entry at all (B2: it used to be computed and discarded AFTER the engine
        // had already mutated), `positioned` whether a position or a working
        // entry already exists.
        public BbAction OnBar(BbBar bar, int secs, DateTime sessionDate,
                              double atr, bool atrWarm, bool canTrade, bool positioned)
        {
            BbAction a = default(BbAction);
            a.Fire = false;
            a.Why = "none";

            _st.BarCount++;
            RollDay(sessionDate);

            // Warmup. Every gate below reads an ATR-scaled threshold, and a
            // partially warmed ATR shrinks all of them at once — which reads as
            // "it took a trade it should not have", never as a warmup bug.
            if (!atrWarm || atr <= 0.0)
            {
                _st.Gate.Set("atr warm", "warming", 0);
                return a;
            }

            // The lifecycle runs on EVERY closed bar — while locked out, while
            // AUTO-TRADE is off, while positioned. The sample ring, the window
            // and a box's age describe the TAPE, not our permission to trade it;
            // suppressing them leaves a hole that re-enabling cannot fill.
            Lifecycle(bar, atr);

            if (_st.SealedCount < _cfg.BoxMeanSamples)
            {
                _st.Gate.Set("box warming",
                             _st.SealedCount + "/" + _cfg.BoxMeanSamples + " boxes sealed", 1);
                return a;
            }

            _st.Gate.Set("box", "no sealed box", 2);
            return a;
        }

        // Called on the FILL, not on the submit: a trigger that never filled
        // consumed no budget, and counting it here is how MaxTradesPerBox = 1
        // silently becomes zero trades on a day of cancelled entries.
        public void OnEntryFilled()
        {
            _st.TradesThisBox++;
            _st.TradesToday++;
            Disarm();
        }

        // The shell refused, cancelled or lost the entry this engine armed. The
        // trade was never taken, so the edge is handed back — preserved from v1
        // (§11 B3/B4) across the rewrite. A no-op today (nothing arms yet) but
        // the shell's OnEntryRejected routing (§4.1) must keep compiling and the
        // reason must keep landing in LastDisarmReason for its log line.
        public void OnEntryRejected(string reason)
        {
            _st.DisarmReason = "refused:" + reason;
            Disarm();
        }

        // The trigger ran out its own clock and the shell has now cancelled the
        // order that went with it. Idempotent, like v1: the shell calls it for
        // the owning engine without asking whether it needs to first.
        public void OnTriggerExpired()
        {
            _st.DisarmReason = "expired";
            Disarm();
        }

        private void RollDay(DateTime sessionDate)
        {
            if (_st.CountedDay == sessionDate)
                return;
            _st.CountedDay = sessionDate;
            _st.TradesToday = 0;
        }

        private void Disarm()
        {
            _st.Armed = false;
            _st.ArmDir = 0;
            _st.ArmTriggerPx = 0.0;
            _st.TriggerArmedBars = 0;
        }

        #region Lifecycle

        private void Lifecycle(BbBar bar, double atr)
        {
            double hi, lo;
            bool full = WindowRange(out hi, out lo);
            Push(bar);
            if (!full)
                return;

            PushSample(hi - lo);
            // Invalidate BEFORE forming, so the bar that buries a box can also
            // be the bar a replacement seals on — a one-bar dead zone after
            // every box is a one-bar dead zone in the only quiet tape the model
            // trades.
            Invalidate(bar, atr);
            Form(bar, hi, lo);
        }

        private void Invalidate(BbBar bar, double atr)
        {
            if (_st.Box == null)
                return;

            _st.BoxAge++;

            // The tolerance is ATR-scaled, not a tick count: one tick through an
            // edge is noise on any tape, and the same absolute number is noise on
            // one instrument and a real break on another.
            double dead = _cfg.BoxDeadAtr * atr;
            bool broken = bar.Close > _st.Box.High + dead || bar.Close < _st.Box.Low - dead;
            bool old = _st.BoxAge > _cfg.BoxMaxAge;
            if (!broken && !old)
                return;

            Disarm();
            _st.Box = null;
            _st.BoxAge = 0;
        }

        // MAX(High,N)[1] − MIN(Low,N)[1]: the window ENDS one bar back, because
        // it is read BEFORE the current bar is pushed.
        private bool WindowRange(out double hi, out double lo)
        {
            hi = 0.0;
            lo = 0.0;
            if (_st.WinFilled < _st.WinHigh.Length)
                return false;

            hi = double.MinValue;
            lo = double.MaxValue;
            for (int i = 0; i < _st.WinHigh.Length; i++)
            {
                if (_st.WinHigh[i] > hi) hi = _st.WinHigh[i];
                if (_st.WinLow[i] < lo) lo = _st.WinLow[i];
            }
            return true;
        }

        private void Push(BbBar bar)
        {
            _st.WinHigh[_st.WinIdx] = bar.High;
            _st.WinLow[_st.WinIdx] = bar.Low;
            _st.WinIdx = (_st.WinIdx + 1) % _st.WinHigh.Length;
            if (_st.WinFilled < _st.WinHigh.Length)
                _st.WinFilled++;
        }

        private void PushSample(double range)
        {
            _st.Samples[_st.SampleIdx] = range;
            _st.SampleIdx = (_st.SampleIdx + 1) % _st.Samples.Length;
            if (_st.SampleFilled < _st.Samples.Length)
                _st.SampleFilled++;
        }

        // Nearest-rank percentile over the filled part of the ring.
        // ponytail: sorts a copy every bar — 200 samples is ~1600 comparisons on
        // a 30s bar. Replace with an order-statistic structure only if a profiler
        // ever names it.
        private double Percentile(double pct)
        {
            int n = _st.SampleFilled;
            if (n < 1)
                return double.NaN;

            double[] copy = new double[n];
            Array.Copy(_st.Samples, copy, n);
            Array.Sort(copy);

            double p = pct < 0.0 ? 0.0 : (pct > 100.0 ? 100.0 : pct);
            int rank = (int)Math.Ceiling(p / 100.0 * n) - 1;
            if (rank < 0) rank = 0;
            if (rank >= n) rank = n - 1;
            return copy[rank];
        }

        private void Form(BbBar bar, double hi, double lo)
        {
            double thr = Percentile(_cfg.BoxRangePctile);
            bool candidate = !double.IsNaN(thr) && (hi - lo) <= thr;

            if (!candidate)
            {
                _st.CandOpen = false;
                _st.CandBars = 0;
                return;
            }

            if (!_st.CandOpen)
            {
                _st.CandOpen = true;
                _st.CandBars = 1;
            }
            else _st.CandBars++;

            _st.CandHigh = hi;
            _st.CandLow = lo;

            if (_st.CandBars < _cfg.BoxMinBars)
                return;

            // A live box is never replaced. It dies first (INVALIDATE) — an
            // object whose identity changes every quiet bar cannot carry a
            // per-Id arm budget, which is the whole of §6.2.
            if (_st.Box != null)
                return;

            // Refuse to seal a box the current bar has already left. The window
            // excludes this bar by design, so without this guard the bar that
            // breaks a box seals an identical one on the spot and the engine
            // churns Ids while price runs away.
            if (bar.Close > hi || bar.Close < lo)
                return;

            Seal(hi - lo, bar.Time);
        }

        private void Seal(double range, DateTime t)
        {
            _st.Box = new BbBox
            {
                High = _st.CandHigh,
                Low = _st.CandLow,
                SealedAt = t,
                Valid = false,              // the validity gate fills this in (Task 44)
                Id = _st.NextBoxId++
            };
            _st.BoxAge = 0;
            _st.ArmsUp = 0;
            _st.ArmsDn = 0;
            _st.TradesThisBox = 0;
            _st.CandOpen = false;
            _st.CandBars = 0;

            _st.SealedRanges[_st.SealedIdx] = range;
            _st.SealedIdx = (_st.SealedIdx + 1) % _st.SealedRanges.Length;
            if (_st.SealedFilled < _st.SealedRanges.Length)
                _st.SealedFilled++;
            _st.SealedCount++;
        }

        #endregion

        private static string F(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
