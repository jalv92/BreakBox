// AveragingEngineCore.cs — SIM-ONLY averaging-down laboratory: the envelope
// solver, the budget-ratchet stop/TP recomputation, and the confirmation state
// machine. Pure decision code; it never touches an order.
//
// ZERO `using NinjaTrader.*`, namespace `BreakBoxCore`, C# 7.3 only — same
// rules and same reason as BreakBoxExits.cs. Any Custom strategy can compose it
// (all of Custom compiles into one assembly); promote to its own namespace only
// when a second host actually exists.
//
// HONEST-USE NOTE (binding, from the 2026-08-19 spec): averaging-down cannot
// create edge — under a driftless price any bounded add schedule has EV = 0,
// so the achieved win rate equals its own break-even threshold. The planned
// loss cap is a MODE, not a maximum: stop slippage multiplies by the full
// stack. And the payoff shape (max unrealized excursion right before the best
// outcome) is the one that breaches 4 of 5 prop firms on unrealized drawdown.
// This module exists to MEASURE those claims in Playback. Its telemetry is the
// product. It must never arm on a live account.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BreakBoxCore
{
    // Where the structural spacing candidate comes from. Auto: the box's own
    // height when the box engine owns the trade and a sealed box exists, the
    // ATR multiple otherwise. Whatever the source, the budget-solved d caps it
    // — structure only ever COMPRESSES the grid.
    public enum AvgSpacingSource { Auto, BoxHeight, AtrMult }

    public sealed class AvgConfig
    {
        public double TickSize = 0.25;
        public double TickValue = 5.0;        // dollars per tick per contract — from the instrument, never assumed
        public int MaxAdds = 2;               // N (host dial clamps 1..2; the engine is general)
        public int AddQty = 1;                // q, flat per add
        public double BudgetDollars = 0.0;    // L_arm — the trade's slice of the daily loss limit
        public double TargetDollars = 150.0;  // G — net profit the trade still aims for
        public int StopBufferTicks = 8;       // s below the deepest level (raised to d/2 at arm)
        public double CommissionRt = 5.76;    // round-turn per contract
        public int SlippageReserveTicks = 2;  // reserved out of the budget for the stack's stop
        public int ConfirmBars = 1;           // closes back beyond a touched level before adding
        public double VolAbortMult = 2.0;     // one-way: ATR above this multiple of entry ATR kills remaining adds
        public const int TP_FLOOR_TICKS = 8;  // TP never collapses inside spread+queue+commission
        public const int MAX_LEVELS = 8;
    }

    // One armed grid. The engine's ONLY memory; nothing it needs may live in the shell.
    public sealed class AvgPlan
    {
        public bool Armed;
        public string RefusedWhy = "";
        public int Dir;                       // +1 long, -1 short
        public double EntryPx;                // P0
        public int EntryQty;                  // q0 — the host's actual fill, not assumed flat
        public int DTicks;                    // solved spacing
        public int STicks;                    // effective stop buffer
        public double LEff;                   // budget net of commissions + slippage reserve
        public double StopPx;                 // LIVE stop: planned grid bottom, ratcheted only toward price
        public double EntryAtr;               // frozen at arm; the vol-abort reference
        public int Levels;                    // == MaxAdds when armed
        public readonly double[] LevelPx = new double[AvgConfig.MAX_LEVELS];
        public readonly bool[] Touched = new bool[AvgConfig.MAX_LEVELS];
        public readonly int[] CloseBacks = new int[AvgConfig.MAX_LEVELS];
        public readonly bool[] Fired = new bool[AvgConfig.MAX_LEVELS];
        public readonly bool[] Dead = new bool[AvgConfig.MAX_LEVELS];
        public bool AddsAborted;              // one-way latch: vol abort, cutoff, or rejection policy
    }

    public struct AvgUpdate
    {
        public double StopPx;
        public double TpPx;
        public bool StopMoved;
        public string Why;
    }

    public static class AvgEngine
    {
        public static double FloorToTick(double px, double tick)
        {
            return Math.Floor(px / tick + 1e-9) * tick;
        }

        public static double CeilToTick(double px, double tick)
        {
            return Math.Ceiling(px / tick - 1e-9) * tick;
        }

        // Solves the grid at the entry fill and freezes it. Refusal is a first-class
        // result: the shell prints RefusedWhy and runs the normal bracket.
        public static AvgPlan Arm(AvgConfig cfg, int dir, double entryPx, int entryQty,
                                  double dStructuralTicks, double entryAtr)
        {
            var p = new AvgPlan();
            if (cfg == null || dir == 0 || entryQty < 1 || cfg.MaxAdds < 1
                || cfg.MaxAdds > AvgConfig.MAX_LEVELS || cfg.AddQty < 1
                || cfg.TickValue <= 0.0 || cfg.TickSize <= 0.0 || entryPx <= 0.0)
            {
                p.RefusedWhy = "bad_inputs";
                return p;
            }

            int n = cfg.MaxAdds, q0 = entryQty, q = cfg.AddQty;
            int qn = q0 + q * n;                       // the full stack
            double v = cfg.TickValue;

            double lEff = cfg.BudgetDollars - cfg.CommissionRt * qn - v * qn * cfg.SlippageReserveTicks;
            if (lEff <= 0.0)
            {
                p.RefusedWhy = "budget_below_costs";
                return p;
            }

            // TP floor (spec §2): the whole-stack TP distance must survive
            // spread + queue + commission. G >= QN * (floor*v - c).
            if (cfg.TargetDollars < qn * (AvgConfig.TP_FLOOR_TICKS * v - cfg.CommissionRt))
            {
                p.RefusedWhy = "target_too_small_for_stack";
                return p;
            }

            // Envelope, generalized to q0 != q. Worst case at stop S = P0 - (N*d + s) ticks:
            //   loss/v = q0*(N*d + s) + q * SUM_{i=1..N} (N*d + s - i*d)
            //          = d * [N*(q0 + q*N) - q*N*(N+1)/2] + s*(q0 + q*N)
            // Solve for d; then enforce s >= d/2 (spec hard constraint) and
            // re-shrink d until (d, s) is stable — s only grows, d only shrinks.
            double denom = n * (double)(q0 + q * n) - q * n * (n + 1) / 2.0;
            int s = cfg.StopBufferTicks < 1 ? 1 : cfg.StopBufferTicks;
            int d = 0;
            for (int iter = 0; iter < 16; iter++)
            {
                double dBudget = (lEff / v - (double)s * (q0 + q * n)) / denom;
                int dNew = (int)Math.Floor(Math.Min(dStructuralTicks, dBudget) + 1e-9);
                int sNew = Math.Max(cfg.StopBufferTicks, (dNew + 1) / 2);
                if (dNew == d && sNew == s)
                    break;
                d = dNew;
                s = sNew;
            }
            // The (d, s) map can 2-cycle instead of settling. Whatever the loop
            // left behind, re-solve d one last time FROM the final s — that
            // direction is the safe one (a d solved from s can never overspend).
            {
                double dBudget = (lEff / v - (double)s * (q0 + q * n)) / denom;
                d = (int)Math.Floor(Math.Min(dStructuralTicks, dBudget) + 1e-9);
            }
            if (d < 1)
            {
                p.RefusedWhy = "budget_too_small_for_grid";
                return p;
            }

            p.Armed = true;
            p.Dir = dir;
            p.EntryPx = entryPx;
            p.EntryQty = entryQty;
            p.DTicks = d;
            p.STicks = s;
            p.LEff = lEff;
            p.EntryAtr = entryAtr;
            p.Levels = n;
            for (int i = 0; i < n; i++)
                p.LevelPx[i] = BbMath.RoundToTick(entryPx - dir * (i + 1) * d * cfg.TickSize, cfg.TickSize);
            p.StopPx = BbMath.RoundToTick(entryPx - dir * (n * d + s) * cfg.TickSize, cfg.TickSize);

            // The envelope is a guarantee, not a hope. If rounding ever broke it,
            // refuse rather than arm an oversized grid.
            if (WorstCaseLoss(cfg, p) > lEff + 1e-6)
            {
                p.Armed = false;
                p.RefusedWhy = "internal_envelope_check";
            }
            return p;
        }

        // Price-loss dollars at the planned stop with every level filled at its
        // planned price. Commissions and slippage live in LEff's derivation, not here.
        public static double WorstCaseLoss(AvgConfig cfg, AvgPlan p)
        {
            if (p == null || !((p.Dir == 1) || (p.Dir == -1)))
                return 0.0;
            double lossTicks = p.EntryQty * (p.EntryPx - p.StopPx) * p.Dir / cfg.TickSize;
            for (int i = 0; i < p.Levels; i++)
                lossTicks += cfg.AddQty * (p.LevelPx[i] - p.StopPx) * p.Dir / cfg.TickSize;
            return lossTicks * cfg.TickValue;
        }

        // Recomputes the live stop and TP from the ACTUAL average and quantity —
        // the plan's table is geometry, this is the guarantee. Confirmation adds
        // fill above their level, so the real average is worse than planned; the
        // stop that spends exactly LEff from the real average then sits above
        // the planned bottom, and it wins. One-way: the stop only ratchets
        // toward price, never away.
        public static AvgUpdate OnFill(AvgConfig cfg, AvgPlan p, double avgPx, int qty)
        {
            double tick = cfg.TickSize;
            int dir = p.Dir;

            double sBudget = avgPx - dir * p.LEff * tick / (cfg.TickValue * qty);
            sBudget = dir > 0 ? CeilToTick(sBudget, tick) : FloorToTick(sBudget, tick);
            bool moved = (sBudget - p.StopPx) * dir > 1e-9;
            if (moved)
                p.StopPx = sBudget;

            // Levels the stop has overtaken can no longer be bought: an add
            // below the stop is a fill the envelope never priced.
            for (int i = 0; i < p.Levels; i++)
                if (!p.Fired[i] && !p.Dead[i]
                    && (p.LevelPx[i] - (p.StopPx + dir * tick)) * dir <= 0.0)
                    p.Dead[i] = true;

            double tp = avgPx + dir * (cfg.TargetDollars + cfg.CommissionRt * qty) * tick / (cfg.TickValue * qty);
            AvgUpdate u;
            u.StopPx = p.StopPx;
            u.TpPx = dir > 0 ? CeilToTick(tp, tick) : FloorToTick(tp, tick);
            u.StopMoved = moved;
            u.Why = moved ? "budget_ratchet" : "fill";
            return u;
        }

        // The confirmation state machine, one CLOSED bar at a time. Touch and
        // reclaim may happen on the same bar — that IS the pattern. Straight-line
        // adverse moves never confirm, which is the entire point: size correlates
        // negatively with trend strength.
        public static int OnBarClosed(AvgConfig cfg, AvgPlan p, double barHigh, double barLow,
                                      double barClose, int[] fireIdx)
        {
            if (p == null || !p.Armed || p.AddsAborted)
                return 0;
            int dir = p.Dir, n = 0;
            for (int i = 0; i < p.Levels; i++)
            {
                if (p.Fired[i] || p.Dead[i])
                    continue;
                double lvl = p.LevelPx[i];
                if ((dir > 0 && barLow <= lvl) || (dir < 0 && barHigh >= lvl))
                    p.Touched[i] = true;
                if (!p.Touched[i])
                    continue;
                bool closedBack = dir > 0 ? barClose >= lvl : barClose <= lvl;
                if (!closedBack)
                {
                    p.CloseBacks[i] = 0;
                    continue;
                }
                p.CloseBacks[i]++;
                if (p.CloseBacks[i] >= cfg.ConfirmBars)
                {
                    p.Fired[i] = true;
                    fireIdx[n++] = i;
                }
            }
            return n;
        }

        // One-way vol abort (spec: adaptivity may only ever REDUCE exposure).
        // True only on the transition so the shell prints once.
        public static bool VolAbort(AvgConfig cfg, AvgPlan p, double atrNow)
        {
            if (p == null || !p.Armed || p.AddsAborted || p.EntryAtr <= 0.0)
                return false;
            if (atrNow <= cfg.VolAbortMult * p.EntryAtr)
                return false;
            p.AddsAborted = true;
            return true;
        }
    }
}
