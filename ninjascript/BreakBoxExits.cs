// BreakBoxExits.cs — the bracket: a structural stop from five selectable
// sources, up to three take-profit tiers priced as R-multiples, breakeven on the
// first tier fill, and a chandelier trail after the second. Pure decision code;
// it never touches an order.
//
// ZERO `using NinjaTrader.*`, own namespace `BreakBoxCore`, C# 7.3 only — same
// rules and same reason as BreakBoxTypes.cs.
//
// WHY R-MULTIPLES. This is the one thing the target's screenshots gave up
// completely (docs/research/01-target-analysis.md §4.1): every take-profit is an
// exact multiple of R = |entry - stop|, verified to the cent on two independent
// samples with wildly different R (15.50 pts and 5.85 pts). The multiples
// themselves DIFFER between the two samples, so they are parameters, not
// constants — which is why Tp1R/Tp2R/Tp3R are three independent dials and NOT
// "Tp1R and a doubling rule", even though TP2 = 2 x TP1 held in both samples.
// Hard-coding the relationship that happened to hold twice would delete the
// degree of freedom the whole finding is about.
//
// WHY THE STOP IS STRUCTURAL. Same section: R differed by 2.6x between samples
// on the same instrument family, which no fixed tick offset produces. The
// target's panel exposes five stop sources (Candle / Swing / MA / E50 / Manual),
// one selector per entry engine. ATR appears on their panel as a number, and
// R / ATR came out at 1.17 in the sample we could measure — consistent with ATR
// being a sanity CLAMP on a structural stop rather than the stop itself. That is
// how it is implemented here: structure first, ATR band second.
//
// TIMING. The bracket is priced at the FILL (you cannot wait a minute to protect
// a position). Breakeven fires from a tier fill — an execution event, not a bar.
// The trail moves only at a BAR CLOSE, which keeps the order layer to at most
// one cancel-replace per bar per trade.
using System;
using System.Collections.Generic;

namespace BreakBoxCore
{
    // The structural candidates the shell measures and hands in. NaN means "this
    // source has nothing to offer right now" — never 0, because 0 is a price.
    public struct BbStopInputs
    {
        public double SignalBarHigh;
        public double SignalBarLow;
        public double LastSwingHigh;
        public double LastSwingLow;
        public double MaValue;
        public double EmaValue;
    }

    public sealed class BbExitConfig
    {
        public double TickSize = 0.25;

        // --- Stop
        public BbStopSource StopSource = BbStopSource.Candle;
        public int StopBufferTicks = 2;         // clearance beyond the structure
        public int ManualStopTicks = 40;        // the `Man` source, and the fallback for every other one
        // The ATR band. Observed R/ATR was 1.17; the band is deliberately wide
        // around it because it exists to reject absurdities (a stop one tick
        // away, a stop 8 ATR away), not to re-derive the stop.
        public double StopMinAtr = 0.5;
        public double StopMaxAtr = 3.0;

        // --- Targets. Tier count 1..3.
        public int TierCount = 3;
        public double Tp1R = 0.5;
        public double Tp2R = 1.0;
        public double Tp3R = 1.5;
        // Percentages of the position taken at tiers 1 and 2; tier 3 takes the
        // remainder. 50/30 -> 50/30/20, which is what a 7-lot splitting 4/2/1
        // rounds to (observed) and what a 10-lot splits 5/3/2.
        public int Tp1Pct = 50;
        public int Tp2Pct = 30;

        // --- Breakeven, fired by the TP1 FILL (the target advertises "breakeven
        // on TP hits"). Not by an R threshold: a threshold and a fill are the
        // same event only when the target sits exactly at the threshold, and the
        // moment TP1R is re-optimised they diverge silently.
        public bool BreakevenOnTp1 = true;
        public int BreakevenOffsetTicks = 1;    // parks this far BEYOND entry, in our favour

        // --- Trail on the runner, armed by the TP2 fill.
        public bool TrailAfterTp2 = true;
        public double TrailAtrMult = 1.5;

        // Not dials: mechanics with no counterpart on the target's panel.
        public const int MIN_TARGET_GAP_TICKS = 2;      // a target inside this of the stop is an inverted OCO
        public const int MAX_TIERS = 3;
    }

    // The bracket's ONLY memory. Nothing it needs may live in the shell.
    public sealed class BbBracket
    {
        public int Dir;                         // +1 long, -1 short, 0 flat
        public double EntryPx;
        public double StopPx;
        public double InitialStopPx;            // never overwritten; R is measured off this
        public double R;                        // |entry - initial stop|, in price
        public double AtrRef;                   // ATR frozen at the entry bar; 0 = was not warm
        public bool Degraded;                   // structure unavailable -> fixed-tick stop for this trade's life
        public string StopWhy = "";             // which source actually priced it

        public readonly double[] TargetPx = new double[BbExitConfig.MAX_TIERS];
        public readonly int[] TargetQty = new int[BbExitConfig.MAX_TIERS];
        public readonly bool[] TierFilled = new bool[BbExitConfig.MAX_TIERS];
        public int Tiers;                       // how many are actually live
        public int QtyTotal;
        public int QtyOpen;

        public bool BeApplied;                  // breakeven is one-shot
        public bool TrailArmed;
        public double Mfe;
        public bool StopCancelled;              // hand-pulled; nothing resurrects it
        public int BarsInTrade;
    }

    public struct BbExitDecision
    {
        public double StopPx;
        public bool StopMoved;
        public bool CloseNow;
        public string Why;                      // init|be|trail|hold|flat
    }

    public static class BbExits
    {
        // Prices the structural stop. Public because the shell needs it BEFORE
        // the fill too: a stop-market entry whose stop would land outside the
        // ATR band is a trade worth refusing at submit time rather than
        // discovering at the fill.
        //
        // Returns the stop PRICE. `why` reports which source won, including
        // "manual_fallback" when the selected source had nothing usable — a
        // silent fallback is how a strategy runs for a week on the wrong stop.
        public static double SeedStop(BbExitConfig cfg, int dir, double entryPx, double atr,
                                      bool atrWarm, BbStopInputs inp, out string why)
        {
            double tick = cfg.TickSize;
            double buf = cfg.StopBufferTicks * tick;
            double raw = double.NaN;
            why = "manual";

            switch (cfg.StopSource)
            {
                case BbStopSource.Candle:
                    raw = dir > 0 ? inp.SignalBarLow - buf : inp.SignalBarHigh + buf;
                    why = "candle";
                    break;
                case BbStopSource.Swing:
                    raw = dir > 0 ? inp.LastSwingLow - buf : inp.LastSwingHigh + buf;
                    why = "swing";
                    break;
                case BbStopSource.Ma:
                    raw = dir > 0 ? inp.MaValue - buf : inp.MaValue + buf;
                    why = "ma";
                    break;
                case BbStopSource.Ema50:
                    raw = dir > 0 ? inp.EmaValue - buf : inp.EmaValue + buf;
                    why = "e50";
                    break;
                case BbStopSource.Manual:
                    raw = entryPx - dir * cfg.ManualStopTicks * tick;
                    why = "manual";
                    break;
            }

            // A structural source is unusable when it is missing, or when it
            // sits on the WRONG SIDE of the entry — a 50-EMA above price on a
            // long is a perfectly ordinary market state, and using it would
            // submit a stop that is already through the market and fills
            // instantly at whatever the next print is.
            bool usable = !double.IsNaN(raw) && !double.IsInfinity(raw) && (entryPx - raw) * dir > 0.0;
            if (!usable)
            {
                raw = entryPx - dir * cfg.ManualStopTicks * tick;
                why = "manual_fallback";
            }

            // The ATR band. Clamps the DISTANCE, keeping the side.
            double dist = Math.Abs(entryPx - raw);
            if (atrWarm && atr > 0.0)
            {
                double lo = cfg.StopMinAtr * atr;
                double hi = cfg.StopMaxAtr * atr;
                if (hi < lo) hi = lo;                       // a degenerate band collapses, it does not invert
                if (dist < lo) { dist = lo; why += "_min"; }
                else if (dist > hi) { dist = hi; why += "_max"; }
            }
            // A zero-width stop is not a configuration, it is an accident.
            if (dist < tick) dist = tick;

            return BbMath.RoundToTick(entryPx - dir * dist, tick);
        }

        // Splits `qty` across the live tiers. Extracted and public because the
        // split is the one piece of arithmetic that must agree between the
        // bracket, the panel's preview and the assert suite.
        //
        // Rounding is floor(x + 0.5) per tier with the LAST live tier taking the
        // remainder. On the observed 7-lot: 3.5 -> 4, 2.1 -> 2, remainder 1 =
        // exactly the 4/2/1 seen on the chart.
        //
        // Tiers that would round to zero are dropped, and the tier count is
        // reduced to `qty` when there is not one contract per tier — three
        // targets on a 2-lot is a configuration that cannot be filled, and
        // silently leaving a 0-quantity order in the list is how a bracket ends
        // up not covering the position.
        public static int SplitTiers(BbExitConfig cfg, int qty, int[] outQty)
        {
            for (int i = 0; i < BbExitConfig.MAX_TIERS; i++)
                outQty[i] = 0;
            if (qty <= 0)
                return 0;

            int tiers = cfg.TierCount;
            if (tiers < 1) tiers = 1;
            if (tiers > BbExitConfig.MAX_TIERS) tiers = BbExitConfig.MAX_TIERS;
            if (tiers > qty) tiers = qty;

            if (tiers == 1)
            {
                outQty[0] = qty;
                return 1;
            }

            int assigned = 0;
            for (int i = 0; i < tiers - 1; i++)
            {
                int pct = i == 0 ? cfg.Tp1Pct : cfg.Tp2Pct;
                int q = (int)Math.Floor(qty * (pct / 100.0) + 0.5);
                if (q < 1) q = 1;
                // Leave at least one contract for every remaining tier.
                int remainingTiers = tiers - 1 - i;
                int max = qty - assigned - remainingTiers;
                if (q > max) q = max;
                outQty[i] = q;
                assigned += q;
            }
            outQty[tiers - 1] = qty - assigned;
            return tiers;
        }

        // Prices the whole bracket at the fill and seeds the state. Called ONCE
        // per trade, from the execution event — not from a bar close.
        public static BbExitDecision OnEntryFill(BbExitConfig cfg, BbBracket br,
                                                 int dir, double fillPx, int qty,
                                                 double atr, bool atrWarm, BbStopInputs inp)
        {
            double tick = cfg.TickSize;

            br.Dir = dir;
            br.EntryPx = fillPx;
            br.AtrRef = atrWarm ? atr : 0.0;
            br.Mfe = fillPx;
            br.QtyTotal = qty;
            br.QtyOpen = qty;
            br.BeApplied = false;
            br.TrailArmed = false;
            br.StopCancelled = false;
            br.BarsInTrade = 0;
            for (int i = 0; i < BbExitConfig.MAX_TIERS; i++)
            {
                br.TargetPx[i] = 0.0;
                br.TierFilled[i] = false;
            }

            string why;
            br.StopPx = SeedStop(cfg, dir, fillPx, atr, atrWarm, inp, out why);
            br.InitialStopPx = br.StopPx;
            br.StopWhy = why;
            br.Degraded = why.StartsWith("manual_fallback", StringComparison.Ordinal);
            br.R = Math.Abs(fillPx - br.StopPx);

            br.Tiers = SplitTiers(cfg, qty, br.TargetQty);

            // Targets are anchored at the ENTRY and priced in R. The R-multiples
            // are read in tier order and forced monotone: a config where TP2
            // lands nearer than TP1 would fill out of order and leave the
            // breakeven trigger attached to whichever one happened to be first.
            double prev = 0.0;
            for (int i = 0; i < br.Tiers; i++)
            {
                double m = i == 0 ? cfg.Tp1R : (i == 1 ? cfg.Tp2R : cfg.Tp3R);
                if (m <= prev) m = prev + (tick / (br.R > 0.0 ? br.R : 1.0));
                prev = m;
                double px = BbMath.RoundToTick(fillPx + dir * m * br.R, tick);
                // Never inside the stop's own gap: an OCO pair that close is a
                // coin flip on which leg fills, and the platform accepts it.
                double floorPx = br.StopPx + dir * BbExitConfig.MIN_TARGET_GAP_TICKS * tick;
                if ((px - floorPx) * dir < 0.0)
                    px = BbMath.RoundToTick(floorPx, tick);
                // And never at or behind the entry itself.
                double minPx = BbMath.RoundToTick(fillPx + dir * BbExitConfig.MIN_TARGET_GAP_TICKS * tick, tick);
                if ((px - minPx) * dir < 0.0)
                    px = minPx;
                br.TargetPx[i] = px;
            }

            BbExitDecision d;
            d.StopPx = br.StopPx;
            d.StopMoved = true;
            d.CloseNow = false;
            d.Why = "init:" + why;
            return d;
        }

        // A take-profit tier filled. Returns whether the stop moved.
        //
        // Breakeven is MONOTONE and one-shot: a stop already dragged past
        // breakeven by hand keeps the human's price. It also refuses to move a
        // stop the human CANCELLED — that latch is permanent.
        public static BbExitDecision OnTierFill(BbExitConfig cfg, BbBracket br, int tier, int qtyFilled)
        {
            BbExitDecision d;
            d.StopPx = br.StopPx;
            d.StopMoved = false;
            d.CloseNow = false;
            d.Why = "tier";

            if (tier >= 0 && tier < BbExitConfig.MAX_TIERS)
                br.TierFilled[tier] = true;
            br.QtyOpen -= qtyFilled;
            if (br.QtyOpen < 0) br.QtyOpen = 0;

            if (br.QtyOpen == 0)
            {
                d.Why = "tier_flat";
                return d;
            }

            if (tier == 0 && cfg.BreakevenOnTp1 && !br.BeApplied && !br.StopCancelled)
            {
                double be = BbMath.RoundToTick(br.EntryPx + br.Dir * cfg.BreakevenOffsetTicks * cfg.TickSize,
                                               cfg.TickSize);
                br.BeApplied = true;
                if ((be - br.StopPx) * br.Dir > 0.0)
                {
                    br.StopPx = be;
                    d.StopPx = be;
                    d.StopMoved = true;
                    d.Why = "be";
                }
            }

            if (tier == 1 && cfg.TrailAfterTp2)
                br.TrailArmed = true;

            return d;
        }

        // The trail, recomputed at the close of `bar`. Called ONCE per closed bar
        // while positioned. Deterministic: no clock, no randomness, no I/O.
        //
        // Monotone chandelier off the MFE: the stop only ever tightens. A
        // BACKWARDS drag by hand is adopted and then re-tightened at the next
        // bar close, because the candidate computed from the MFE will beat the
        // loosened price. That is intended ("the stop never goes backwards" is a
        // rule about the algorithm, not about the operator).
        public static BbExitDecision OnBarClose(BbExitConfig cfg, BbBracket br, BbBar bar, double atr)
        {
            BbExitDecision d;
            d.StopPx = br.StopPx;
            d.StopMoved = false;
            d.CloseNow = false;
            d.Why = "hold";

            if (br.Dir == 0)
            {
                d.Why = "flat";
                return d;
            }

            br.BarsInTrade++;

            double ext = br.Dir > 0 ? bar.High : bar.Low;
            if ((ext - br.Mfe) * br.Dir > 0.0)
                br.Mfe = ext;

            if (!br.TrailArmed || br.StopCancelled)
                return d;

            // The trail uses the LIVE ATR, not the frozen entry ATR: it is a
            // statement about current volatility, and a runner held through a
            // regime change should be trailed by the regime it is in. The frozen
            // AtrRef stays the reference for R, which must not move.
            double a = atr > 0.0 ? atr : br.AtrRef;
            if (a <= 0.0)
                return d;

            double cand = BbMath.RoundToTick(br.Mfe - br.Dir * cfg.TrailAtrMult * a, cfg.TickSize);
            if ((cand - br.StopPx) * br.Dir > 0.0)
            {
                br.StopPx = cand;
                d.StopPx = cand;
                d.StopMoved = true;
                d.Why = "trail";
            }
            return d;
        }

        // Adopts a stop the human moved (or pulled) in Chart Trader. Writes the
        // price and the cancel latch and NOTHING ELSE.
        public static void AdoptManualStop(BbBracket br, double stopPx, bool cancelled)
        {
            br.StopPx = stopPx;
            br.StopCancelled = cancelled;
        }

        // How many contracts the remaining tiers still cover. The shell compares
        // this against the real position every bar: a mismatch means a leg was
        // rejected or cancelled and the position is partly naked, which is the
        // one bracket failure that costs real money.
        public static int CoveredQty(BbBracket br)
        {
            int covered = 0;
            for (int i = 0; i < br.Tiers; i++)
                if (!br.TierFilled[i])
                    covered += br.TargetQty[i];
            return covered;
        }
    }
}
