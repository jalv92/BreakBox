// BreakBoxCore.cs — the box and the two entry engines. Pure decision code; it
// never touches an order, never reads a clock, never does I/O.
//
// ZERO `using NinjaTrader.*`, own namespace `BreakBoxCore`, C# 7.3 only — same
// rules and same reason as BreakBoxTypes.cs.
//
// WHAT A BOX IS. A price range built from a completed period, projected forward,
// and traded either on its break or on the retrace back to its edge after a real
// extension. Three sources, selectable, because the target exposes exactly this
// idea three ways: a 4H prior-period box on the chart (`PH High` / `PH Low`), an
// Initial Balance in the bundled IB bot, and per-session windows in the
// optimiser. They are the same object with different anchors.
//
// THE BOX IS BUILT HERE, NOT FROM A SECOND DATA SERIES. Feeding 1m bars and
// folding them into HTF slots inside the engine costs ~30 lines and removes the
// entire AddDataSeries fold-index problem from the shell — and, more to the
// point, lets the test runner build a box without NT8 in the room. The slots are
// anchored to the ETH session open (18:00 ET), which is where NT8 anchors its
// own 4H bars, so a 240-minute slot here lines up with a 240-minute bar there.
//
// TIMING. Every decision is taken at a BAR CLOSE, from closed bars only. The
// engine never sees an intra-bar price, so nothing it returns can depend on one.
using System;
using System.Collections.Generic;

namespace BreakBoxCore
{
    public enum BbBoxSource
    {
        PriorPeriod = 0,        // the previous HTF slot (the chart's 4H `PH High`/`PH Low` box)
        InitialBalance = 1,     // the first N minutes after the session's cash open
        PriorSession = 2        // the whole previous session
    }

    public enum BbEntryEngine
    {
        Break = 0,              // trade the break of the edge
        Retrace = 1             // let it extend, then trade the return to the edge
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
        public DateTime AnchorStart;    // first bar of the period the box describes
        public DateTime AnchorEnd;      // the bar that closed it
        public bool Valid;              // range passed the ATR sanity band
        public int Id;                  // monotone; the shell keys per-box trade counts off this

        public double Range { get { return High - Low; } }
    }

    public sealed class BbConfig
    {
        public double TickSize = 0.25;

        // --- Box construction
        public BbBoxSource BoxSource = BbBoxSource.PriorPeriod;
        public int HtfMinutes = 240;            // PriorPeriod slot width, anchored to the session open
        public int SessionOpenHhmm = 1800;      // ETH open, ET. The anchor for every slot boundary.
        public int IbStartHhmm = 930;           // InitialBalance: the cash open
        public int IbMinutes = 60;
        public int SessionCloseHhmm = 1700;     // PriorSession rolls here

        // A box far outside the ordinary range is not a box: an 8-ATR overnight
        // slot is a trend leg with two arbitrary ends, and a 0.2-ATR one is
        // noise whose "break" is one tick of drift. Both produce trades the
        // model was never about, so both are refused outright rather than
        // sized down.
        public double MinBoxRangeAtr = 0.5;
        public double MaxBoxRangeAtr = 6.0;

        // --- Engines (independent toggles, exactly like the target's panel)
        public bool EnableBreak = true;
        public bool EnableRetrace = false;
        public bool AllowLong = true;
        public bool AllowShort = true;

        // --- Break engine
        // The trigger sits BEYOND the edge and is a stop order, matching the
        // observed fills (signal 26853.00 -> fill 26853.75, +3 ticks; signal
        // 23713.75 -> fill 23713.50 on a short). A limit at the edge is a
        // different model — it wins the mean-reverting cases and loses every
        // real break — and it is not what the target does.
        public int BreakBufferTicks = 4;
        // Require the bar to CLOSE outside. A wick through the edge is the
        // single most common way a box breakout backtest lies to you: it counts
        // the touch as a break and the reversal as bad luck.
        public bool RequireCloseOutside = true;
        // The trigger is not live forever. If price closes back inside and stays
        // there, the break thesis is dead and a resting stop order becomes a
        // random re-entry days later.
        public int TriggerLifeBars = 5;

        // --- Retrace engine
        // What counts as a REAL extension before we are willing to buy the pull
        // back. The bundled IB bot's whole pitch is "waits for a real extension,
        // then enters the retrace instead of chasing".
        public double ExtensionAtr = 1.0;
        // How long the retrace may take. Past this the extension is old news and
        // the edge is just a level price happens to be near.
        public int RetraceMaxBars = 30;
        // The limit sits this far INSIDE the box from the edge (0 = at the edge).
        public int RetraceOffsetTicks = 0;
        // Refuse the retrace if price has closed back through the far side of
        // the box: that is not a retrace to the breakout edge, it is a failed
        // break traversing the range.
        public bool RetraceAbortOnFarSide = true;

        // --- Gating
        public int MaxTradesPerBox = 1;
        public int MaxTradesPerDay = 5;
        public int EntryWindowStartHhmm = 1800;   // 18:00 -> 17:00 = the whole ETH session
        public int EntryWindowEndHhmm = 1700;
        public int AtrPeriod = 14;
    }

    public struct BbAction
    {
        public bool Fire;
        public int Dir;                 // +1 long, -1 short
        public BbEntryEngine Engine;
        public double TriggerPx;        // stop price (Break) or limit price (Retrace)
        public bool IsLimit;            // false => stop-market
        public double SignalBarHigh;    // the `Candle` stop source reads these two
        public double SignalBarLow;
        public double BoxHigh, BoxLow;
        public int BoxId;
        public string Why;
    }

    // The engine's ONLY memory. Nothing it needs may live in the shell.
    public sealed class BbEngineState
    {
        // Box accumulation
        public double AccHigh = double.NaN;
        public double AccLow = double.NaN;
        public DateTime AccStart = DateTime.MinValue;
        public long AccSlot = long.MinValue;
        public bool AccOpen;

        public BbBox Box;
        public int NextBoxId = 1;

        // Break state, per direction. `Armed` means a trigger is live.
        public bool BreakArmed;
        public int BreakDir;
        public double BreakTriggerPx;
        public int BreakArmedBars;
        // The break of THIS edge has already been taken (armed, filled or
        // expired) and must not be re-armed until price returns inside the box.
        // Without this latch a sustained break re-arms on every single bar: the
        // trigger expires on its bar budget and the very next check sees a close
        // outside the edge and arms a fresh one, forever.
        public int BreakSpentDir;

        // Retrace state
        public int ExtDir;                      // direction of the excursion being tracked, 0 = none
        public double ExtEdge;                  // the edge it left from
        public double ExtBest = double.NaN;     // furthest price beyond the edge
        public bool ExtQualified;               // excursion reached ExtensionAtr
        public int ExtBars;

        // Counters
        public int TradesThisBox;
        public int TradesToday;
        public int LastCountedBoxId = -1;
        public DateTime CountedDay = DateTime.MinValue;
    }

    public sealed class BbEngine
    {
        private readonly BbConfig _cfg;
        private readonly BbEngineState _st;

        public BbEngine(BbConfig cfg, BbEngineState st)
        {
            _cfg = cfg;
            _st = st;
        }

        public BbBox Box { get { return _st.Box; } }
        public BbEngineState State { get { return _st; } }

        // Feed one CLOSED bar. `secs` is its ET seconds-of-day, `sessionDate`
        // the trading day it belongs to (used only for the daily trade counter),
        // `atr` the warm house ATR, `positioned` whether the shell already holds
        // a position (the engine still tracks the box and the excursion while
        // positioned — it just cannot fire).
        public BbAction OnBar(BbBar bar, int secs, DateTime sessionDate, double atr, bool atrWarm, bool positioned)
        {
            BbAction a = default(BbAction);
            a.Fire = false;
            a.Why = "none";

            RollDay(sessionDate);
            Accumulate(bar, secs, atr, atrWarm);

            if (_st.Box == null || !_st.Box.Valid || !atrWarm || atr <= 0.0)
                return a;

            // A new box resets the per-box counter and every armed trigger: the
            // level they referenced no longer exists.
            if (_st.LastCountedBoxId != _st.Box.Id)
            {
                _st.LastCountedBoxId = _st.Box.Id;
                _st.TradesThisBox = 0;
                DisarmBreak("newbox");
                ClearExcursion();
            }

            TrackExcursion(bar, atr);

            bool windowOpen = BbMath.InWindow(secs, BbMath.HhmmToSecs(_cfg.EntryWindowStartHhmm),
                                                    BbMath.HhmmToSecs(_cfg.EntryWindowEndHhmm));
            bool budget = _st.TradesThisBox < _cfg.MaxTradesPerBox && _st.TradesToday < _cfg.MaxTradesPerDay;

            // Trigger bookkeeping runs even when we cannot trade, so an armed
            // break expires on schedule instead of surviving a whole flat period
            // and firing into a stale level.
            if (_st.BreakArmed)
            {
                _st.BreakArmedBars++;
                if (_st.BreakArmedBars > _cfg.TriggerLifeBars)
                    DisarmBreak("expired");
                else if (BackInside(bar))
                    DisarmBreak("reentered");
            }

            // The spent latch clears only when price is back INSIDE the box.
            // This is what stops a sustained break from re-arming on every bar
            // for the rest of the session once its first trigger expired.
            if (_st.BreakSpentDir != 0 && InsideBox(bar, _st.Box))
                _st.BreakSpentDir = 0;

            if (positioned || !windowOpen || !budget)
                return a;

            if (_cfg.EnableBreak)
            {
                a = TryBreak(bar);
                if (a.Fire) return Stamp(a);
            }
            if (_cfg.EnableRetrace)
            {
                a = TryRetrace(bar, atr);
                if (a.Fire) return Stamp(a);
            }

            a.Fire = false;
            a.Why = "none";
            return a;
        }

        // The shell calls this when an entry actually FILLS, not when it is
        // submitted: a trigger that never filled consumed no budget, and
        // counting it there is how "MaxTradesPerBox = 1" silently becomes zero
        // trades on a day of cancelled entries.
        public void OnEntryFilled()
        {
            _st.TradesThisBox++;
            _st.TradesToday++;
            DisarmBreak("filled");
            ClearExcursion();
        }

        #region Box construction

        private void RollDay(DateTime sessionDate)
        {
            if (_st.CountedDay == sessionDate)
                return;
            _st.CountedDay = sessionDate;
            _st.TradesToday = 0;
        }

        // Which accumulation slot does this bar belong to? Returns long.MinValue
        // when the bar is outside any slot the current source cares about (e.g.
        // outside the IB window), which closes the open accumulator.
        private long SlotOf(BbBar bar, int secs)
        {
            int openSecs = BbMath.HhmmToSecs(_cfg.SessionOpenHhmm);

            if (_cfg.BoxSource == BbBoxSource.PriorPeriod)
            {
                if (_cfg.HtfMinutes < 1)
                    return long.MinValue;
                // Minutes since the most recent session open, on a wrapping
                // clock. Anchoring on the session open (not on midnight) is what
                // makes a 240-minute slot here agree with NT8's own 4H bar.
                int delta = secs - openSecs;
                if (delta < 0) delta += 24 * 3600;
                long dayKey = bar.Time.AddSeconds(-delta).Date.Ticks;
                return dayKey + delta / (_cfg.HtfMinutes * 60);
            }

            if (_cfg.BoxSource == BbBoxSource.InitialBalance)
            {
                int ibStart = BbMath.HhmmToSecs(_cfg.IbStartHhmm);
                int ibEnd = (ibStart + _cfg.IbMinutes * 60) % (24 * 3600);
                if (!BbMath.InWindow(secs, ibStart, ibEnd))
                    return long.MinValue;
                return bar.Time.Date.Ticks;
            }

            // PriorSession: one slot per ETH session, keyed by the date the
            // session STARTED on.
            int close = BbMath.HhmmToSecs(_cfg.SessionCloseHhmm);
            int off = secs - close;
            if (off < 0) off += 24 * 3600;
            return bar.Time.AddSeconds(-off).Date.Ticks;
        }

        private void Accumulate(BbBar bar, int secs, double atr, bool atrWarm)
        {
            long slot = SlotOf(bar, secs);

            if (slot != _st.AccSlot)
            {
                if (_st.AccOpen)
                    Seal(atr, atrWarm);
                _st.AccSlot = slot;
                _st.AccOpen = slot != long.MinValue;
                _st.AccHigh = double.NaN;
                _st.AccLow = double.NaN;
                _st.AccStart = bar.Time;
            }

            if (!_st.AccOpen)
                return;

            if (double.IsNaN(_st.AccHigh) || bar.High > _st.AccHigh) _st.AccHigh = bar.High;
            if (double.IsNaN(_st.AccLow) || bar.Low < _st.AccLow) _st.AccLow = bar.Low;
        }

        private void Seal(double atr, bool atrWarm)
        {
            if (double.IsNaN(_st.AccHigh) || double.IsNaN(_st.AccLow))
                return;

            double range = _st.AccHigh - _st.AccLow;
            bool valid = atrWarm && atr > 0.0
                         && range >= _cfg.MinBoxRangeAtr * atr
                         && range <= _cfg.MaxBoxRangeAtr * atr;

            _st.Box = new BbBox
            {
                High = _st.AccHigh,
                Low = _st.AccLow,
                AnchorStart = _st.AccStart,
                AnchorEnd = DateTime.MinValue,
                Valid = valid,
                Id = _st.NextBoxId++
            };
        }

        #endregion

        #region Break engine

        private BbAction TryBreak(BbBar bar)
        {
            BbAction a = default(BbAction);
            a.Fire = false;
            a.Why = "none";

            double tick = _cfg.TickSize;
            double buf = _cfg.BreakBufferTicks * tick;
            BbBox box = _st.Box;

            // Already armed: the trigger stands, the shell holds a working stop
            // order. Nothing to fire again.
            if (_st.BreakArmed)
                return a;

            bool upBreak = _cfg.RequireCloseOutside ? bar.Close > box.High : bar.High > box.High;
            bool downBreak = _cfg.RequireCloseOutside ? bar.Close < box.Low : bar.Low < box.Low;

            // This edge's break was already taken and price has not been back
            // inside since. Re-arming here is the bug that turns one expired
            // trigger into a fresh trigger on every subsequent bar.
            if (upBreak && _st.BreakSpentDir > 0) upBreak = false;
            if (downBreak && _st.BreakSpentDir < 0) downBreak = false;

            if (upBreak && _cfg.AllowLong)
            {
                // The trigger sits beyond the BREAK BAR's extreme, not beyond
                // the box edge: on the bar that closes 12 points through the
                // level, a trigger at edge+1 tick is already deep inside the
                // market and fills instantly at whatever the next print is.
                double trig = BbMath.RoundToTick(Math.Max(bar.High, box.High) + buf, tick);
                ArmBreak(+1, trig);
                a.Fire = true; a.Dir = +1; a.Engine = BbEntryEngine.Break;
                a.TriggerPx = trig; a.IsLimit = false; a.Why = "break_up";
            }
            else if (downBreak && _cfg.AllowShort)
            {
                double trig = BbMath.RoundToTick(Math.Min(bar.Low, box.Low) - buf, tick);
                ArmBreak(-1, trig);
                a.Fire = true; a.Dir = -1; a.Engine = BbEntryEngine.Break;
                a.TriggerPx = trig; a.IsLimit = false; a.Why = "break_dn";
            }

            if (a.Fire)
            {
                a.SignalBarHigh = bar.High;
                a.SignalBarLow = bar.Low;
            }
            return a;
        }

        private void ArmBreak(int dir, double trig)
        {
            _st.BreakArmed = true;
            _st.BreakDir = dir;
            _st.BreakTriggerPx = trig;
            _st.BreakArmedBars = 0;
            _st.BreakSpentDir = dir;
        }

        private void DisarmBreak(string why)
        {
            _st.BreakArmed = false;
            _st.BreakDir = 0;
            _st.BreakTriggerPx = 0.0;
            _st.BreakArmedBars = 0;
        }

        // Has price closed back inside the box, killing the break thesis?
        private bool BackInside(BbBar bar)
        {
            BbBox box = _st.Box;
            if (box == null) return false;
            return _st.BreakDir > 0 ? bar.Close < box.High : bar.Close > box.Low;
        }

        private static bool InsideBox(BbBar bar, BbBox box)
        {
            return box != null && bar.Close <= box.High && bar.Close >= box.Low;
        }

        public bool BreakArmed { get { return _st.BreakArmed; } }
        public double BreakTriggerPx { get { return _st.BreakTriggerPx; } }
        public int BreakDir { get { return _st.BreakDir; } }

        #endregion

        #region Retrace engine

        // Tracks how far price has travelled beyond a box edge and whether that
        // excursion has qualified as a real extension. Runs on EVERY bar,
        // including while positioned, so the state is continuous.
        private void TrackExcursion(BbBar bar, double atr)
        {
            BbBox box = _st.Box;

            if (_st.ExtDir == 0)
            {
                if (bar.Close > box.High) StartExcursion(+1, box.High, bar.High);
                else if (bar.Close < box.Low) StartExcursion(-1, box.Low, bar.Low);
                // Fall through and qualify on THIS bar. A single explosive bar
                // that leaves the box by 2 ATR and closes there is the textbook
                // extension; deferring the check to the next bar means the one
                // move most worth fading never qualifies, because by then the
                // excursion's best price is already history.
                Qualify(atr);
                return;
            }

            _st.ExtBars++;

            // Aborted: back through the far edge is a range traversal, not a
            // pullback to the level we broke.
            if (_cfg.RetraceAbortOnFarSide)
            {
                bool through = _st.ExtDir > 0 ? bar.Close < box.Low : bar.Close > box.High;
                if (through) { ClearExcursion(); return; }
            }

            if (_st.ExtBars > _cfg.RetraceMaxBars)
            {
                ClearExcursion();
                return;
            }

            double ext = _st.ExtDir > 0 ? bar.High : bar.Low;
            if ((ext - _st.ExtBest) * _st.ExtDir > 0.0)
                _st.ExtBest = ext;

            Qualify(atr);
        }

        private void Qualify(double atr)
        {
            if (_st.ExtDir == 0 || _st.ExtQualified)
                return;
            if ((_st.ExtBest - _st.ExtEdge) * _st.ExtDir >= _cfg.ExtensionAtr * atr)
                _st.ExtQualified = true;
        }

        private void StartExcursion(int dir, double edge, double ext)
        {
            _st.ExtDir = dir;
            _st.ExtEdge = edge;
            _st.ExtBest = ext;
            _st.ExtQualified = false;
            _st.ExtBars = 0;
        }

        private void ClearExcursion()
        {
            _st.ExtDir = 0;
            _st.ExtEdge = 0.0;
            _st.ExtBest = double.NaN;
            _st.ExtQualified = false;
            _st.ExtBars = 0;
        }

        private BbAction TryRetrace(BbBar bar, double atr)
        {
            BbAction a = default(BbAction);
            a.Fire = false;
            a.Why = "none";

            if (_st.ExtDir == 0 || !_st.ExtQualified)
                return a;

            // The bar that OPENED the excursion may not fire its own retrace.
            // Its low (on an up-move) is by construction near the edge it just
            // left, so without this every extension bar is also a retrace
            // signal — entering at the edge on the way OUT, which is the exact
            // opposite of the model.
            if (_st.ExtBars < 1)
                return a;

            int dir = _st.ExtDir;
            if (dir > 0 && !_cfg.AllowLong) return a;
            if (dir < 0 && !_cfg.AllowShort) return a;

            // The retrace is live once price has come back to within one tick of
            // the edge. We do NOT wait for a touch of the limit price itself —
            // that is the order's job. Firing when the bar's range reaches back
            // to the edge is what puts the limit there in time to be filled.
            double tick = _cfg.TickSize;
            double reach = dir > 0 ? bar.Low : bar.High;
            if ((reach - _st.ExtEdge) * dir > atr * 0.25)
                return a;                       // still too far out to place the order usefully

            double limitPx = BbMath.RoundToTick(_st.ExtEdge - dir * _cfg.RetraceOffsetTicks * tick, tick);

            a.Fire = true;
            a.Dir = dir;
            a.Engine = BbEntryEngine.Retrace;
            a.TriggerPx = limitPx;
            a.IsLimit = true;
            a.SignalBarHigh = bar.High;
            a.SignalBarLow = bar.Low;
            a.Why = dir > 0 ? "retrace_up" : "retrace_dn";
            return a;
        }

        public bool RetraceQualified { get { return _st.ExtDir != 0 && _st.ExtQualified; } }

        #endregion

        private BbAction Stamp(BbAction a)
        {
            a.BoxHigh = _st.Box.High;
            a.BoxLow = _st.Box.Low;
            a.BoxId = _st.Box.Id;
            return a;
        }
    }
}
