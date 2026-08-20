// AveragingTests — the envelope solver and its refusals. Every expected number
// is hand arithmetic from the spec (docs/superpowers/specs/2026-08-19-averaging-lab-design.md §2).
using System;
using BreakBoxCore;

public static class AveragingTests
{
    public static void Run()
    {
        T.Section("Averaging: envelope solver");
        EnvelopeSolvesSpacingNetOfCosts();
        WorstCaseIsMonotoneAndCapped();
        ParametersDoNotPortAcrossTickValue();
        RefusalsNameTheBindingConstraint();
        SmallBufferIsRaisedToHalfSpacing();

        T.Section("Averaging: fill ratchet + confirmation");
        RatchetSpendsTheBudgetFromTheRealAverage();
        TpHoldsConstantNetDollars();
        StraightLineNeverConfirms();
        DipAndReclaimFiresOnce();
        VolAbortIsOneWay();

        T.Section("Averaging: telemetry");
        SerialiseIsOneInvariantJsonLine();
    }

    private static AvgConfig NqCfg()
    {
        var c = new AvgConfig();
        c.TickSize = 0.25;
        c.TickValue = 5.0;           // NQ
        c.MaxAdds = 4;               // pure engine is general; the HOST dial clamps 1..2
        c.AddQty = 1;
        c.StopBufferTicks = 16;
        c.CommissionRt = 5.76;
        c.SlippageReserveTicks = 2;
        c.ConfirmBars = 1;
        c.VolAbortMult = 2.0;
        c.TargetDollars = 200.0;     // clears the TP floor: QN=5 needs G >= 5*(8*5-5.76) = 171.20
        return c;
    }

    // Spec §2 worked example: LEff/v = 200 ticks-of-budget, q0=q=1, N=4, s=16 -> d = 12.
    // Budget is chosen so LEff nets to exactly $1000:
    //   QN = 1 + 1*4 = 5; commissions = 5*5.76 = 28.80; slip reserve = 5*5*2 = 50.00
    //   Budget = 1000 + 28.80 + 50.00 = 1078.80
    private static void EnvelopeSolvesSpacingNetOfCosts()
    {
        var c = NqCfg();
        c.BudgetDollars = 1078.80;
        var p = AvgEngine.Arm(c, 1, 20000.00, 1, 1000.0 /* structural wide open */, 12.0);
        T.Check(p.Armed, "arms on a solvable budget");
        T.CheckInt(p.DTicks, 12, "d = 12 ticks (NQ, LEff $1000, N=4, s=16)");
        T.CheckInt(p.STicks, 16, "s stays at the user's 16 (>= d/2)");
        T.CheckClose(p.LEff, 1000.0, "LEff nets commissions and slippage reserve", 1e-6);
        T.CheckClose(p.StopPx, 20000.00 - (4 * 12 + 16) * 0.25, "stop at grid bottom", 1e-9);
        T.CheckClose(p.LevelPx[0], 20000.00 - 12 * 0.25, "level 1 at P0 - d", 1e-9);
        T.CheckClose(p.LevelPx[3], 20000.00 - 48 * 0.25, "level 4 at P0 - 4d", 1e-9);

        // Structural spacing narrower than the budget's -> structure wins (grid compresses, never widens).
        var p2 = AvgEngine.Arm(c, 1, 20000.00, 1, 8.0, 12.0);
        T.CheckInt(p2.DTicks, 8, "d = min(structural, budget)");
    }

    // The full grid is the worst case and it equals LEff exactly (quant proof:
    // each fill adds a strictly positive term; loss(k) is monotone in k).
    private static void WorstCaseIsMonotoneAndCapped()
    {
        var c = NqCfg();
        c.BudgetDollars = 1078.80;
        var p = AvgEngine.Arm(c, 1, 20000.00, 1, 1000.0, 12.0);
        // hand ladder from the spec review: k=0..4 -> 320/580/780/920/1000
        double[] expect = { 320, 580, 780, 920, 1000 };
        for (int k = 0; k <= 4; k++)
        {
            double loss = 1 * (20000.00 - p.StopPx) / 0.25 * 5.0;      // entry contract
            for (int i = 0; i < k; i++)
                loss += 1 * (p.LevelPx[i] - p.StopPx) / 0.25 * 5.0;    // each filled add
            T.CheckClose(loss, expect[k], "loss after " + k + " fills = $" + expect[k], 1e-6);
        }
        T.CheckClose(AvgEngine.WorstCaseLoss(c, p), 1000.0, "WorstCaseLoss == LEff at the full grid", 1e-6);
    }

    private static void ParametersDoNotPortAcrossTickValue()
    {
        var c = NqCfg();
        c.BudgetDollars = 1078.80;
        c.TickValue = 0.50;          // MNQ
        c.CommissionRt = 1.34;
        var p = AvgEngine.Arm(c, 1, 20000.00, 1, 100000.0, 12.0);
        T.Check(p.Armed, "MNQ arms");
        T.Check(p.DTicks > 100, "same dollars -> ~10x wider grid on MNQ (d=" + p.DTicks + ")");
    }

    private static void RefusalsNameTheBindingConstraint()
    {
        var c = NqCfg();
        c.BudgetDollars = 70.0;      // below commissions+slip of the full stack (78.80)
        var p = AvgEngine.Arm(c, 1, 20000.00, 1, 1000.0, 12.0);
        T.Check(!p.Armed && p.RefusedWhy == "budget_below_costs", "budget below costs refuses");

        c.BudgetDollars = 480.0;     // LEff ~ 401: d = (401/5 - 16*5)/denom < 1 -> grid can't fit
        p = AvgEngine.Arm(c, 1, 20000.00, 1, 1000.0, 12.0);
        T.Check(!p.Armed && p.RefusedWhy == "budget_too_small_for_grid", "sub-tick spacing refuses");

        c.BudgetDollars = 1078.80;
        c.TargetDollars = 100.0;     // < 171.20 floor for the 5-lot stack on NQ
        p = AvgEngine.Arm(c, 1, 20000.00, 1, 1000.0, 12.0);
        T.Check(!p.Armed && p.RefusedWhy == "target_too_small_for_stack", "TP floor refuses");

        p = AvgEngine.Arm(NqCfg(), 0, 20000.00, 1, 1000.0, 12.0);
        T.Check(!p.Armed && p.RefusedWhy == "bad_inputs", "dir 0 refuses");
    }

    // Spec hard constraint: s >= d/2. A tiny user buffer is raised, and the raise
    // re-shrinks d until the pair is stable — the worst case must stay <= LEff.
    private static void SmallBufferIsRaisedToHalfSpacing()
    {
        var c = NqCfg();
        c.BudgetDollars = 1078.80;
        c.StopBufferTicks = 2;
        var p = AvgEngine.Arm(c, 1, 20000.00, 1, 1000.0, 12.0);
        T.Check(p.Armed, "arms after raising s");
        T.Check(p.STicks * 2 >= p.DTicks, "s >= d/2 enforced (s=" + p.STicks + " d=" + p.DTicks + ")");
        T.Check(AvgEngine.WorstCaseLoss(c, p) <= p.LEff + 1e-6, "worst case still <= LEff");
    }

    private static AvgPlan ArmedNq(out AvgConfig c)
    {
        c = NqCfg();
        c.MaxAdds = 2;               // the host's real shape
        c.StopBufferTicks = 8;
        c.BudgetDollars = 600.0;
        c.TargetDollars = 200.0;     // QN=3 floor: 3*(40-5.76) = 102.72
        var p = AvgEngine.Arm(c, 1, 20000.00, 1, 1000.0, 12.0);
        T.Check(p.Armed, "fixture arms");
        return p;
    }

    // Entry-only fill: budget stop from the average sits BELOW the planned grid
    // bottom (fewer contracts, more room) -> the planned bottom wins and nothing moves.
    // A worse-than-planned add average pushes the budget stop ABOVE the planned
    // bottom -> the stop ratchets up, and it never comes back down.
    private static void RatchetSpendsTheBudgetFromTheRealAverage()
    {
        AvgConfig c;
        var p = ArmedNq(out c);
        double plannedStop = p.StopPx;

        var u0 = AvgEngine.OnFill(c, p, 20000.00, 1);
        T.CheckClose(u0.StopPx, plannedStop, "entry fill keeps the planned bottom", 1e-9);
        T.Check(!u0.StopMoved, "no ratchet on the entry fill");

        // Full stack at a BAD average (adds chased far above their levels):
        // the stop that spends exactly LEff from the real average sits above
        // the planned bottom, and it wins. LEff = 600 - 3*5.76 - 3*5*2 = 552.72,
        // so at avg 19998.00 / qty 3: 19998 - 552.72*0.25/15 = 19988.788 -> 19989.00.
        var u1 = AvgEngine.OnFill(c, p, 19998.00, 3);
        double expected = AvgEngine.CeilToTick(19998.00 - p.LEff * 0.25 / (5.0 * 3), 0.25);
        T.Check((expected - plannedStop) * p.Dir > 0, "fixture sanity: budget stop is above the plan");
        T.CheckClose(u1.StopPx, expected, "stop ratchets to spend exactly LEff from the real average", 1e-9);
        T.Check(u1.StopMoved, "ratchet reports the move");

        // A later, BETTER recompute must not loosen it: 19997 / qty 3 allows a
        // lower stop, but the ratchet is one-way.
        var u2 = AvgEngine.OnFill(c, p, 19997.00, 3);
        T.CheckClose(u2.StopPx, u1.StopPx, "stop never moves away from price", 1e-9);
        T.Check(!u2.StopMoved, "no move reported when the ratchet holds");
    }

    private static void TpHoldsConstantNetDollars()
    {
        AvgConfig c;
        var p = ArmedNq(out c);
        var u1 = AvgEngine.OnFill(c, p, 20000.00, 1);
        // (G + c*Q) / (Q*v) ticks above the average, rounded away from it
        double t1 = (200.0 + 5.76 * 1) / (1 * 5.0);
        T.CheckClose(u1.TpPx, AvgEngine.CeilToTick(20000.00 + t1 * 0.25, 0.25), "TP at entry qty", 1e-9);
        double avg2 = 19998.50;
        var u2 = AvgEngine.OnFill(c, p, avg2, 3);
        double t3 = (200.0 + 5.76 * 3) / (3 * 5.0);
        T.CheckClose(u2.TpPx, AvgEngine.CeilToTick(avg2 + t3 * 0.25, 0.25), "TP re-anchors on the real average", 1e-9);
        T.Check(t3 < t1, "per-stack TP distance shrinks as contracts grow");
    }

    private static void StraightLineNeverConfirms()
    {
        AvgConfig c;
        var p = ArmedNq(out c);
        var idx = new int[AvgConfig.MAX_LEVELS];
        // five bars straight down through both levels, every close below them
        double px = 20000.00;
        for (int i = 0; i < 5; i++)
        {
            px -= 10 * 0.25;
            int n = AvgEngine.OnBarClosed(c, p, px + 0.25, px - 0.25, px - 0.25, idx);
            T.CheckInt(n, 0, "straight-line bar " + i + " fires nothing");
        }
        T.Check(p.Touched[0] && p.Touched[1], "levels were touched on the way down");
    }

    private static void DipAndReclaimFiresOnce()
    {
        AvgConfig c;
        var p = ArmedNq(out c);
        var idx = new int[AvgConfig.MAX_LEVELS];
        double lvl = p.LevelPx[0];
        // one bar dips through level 1 and closes back above it -> fires level 0
        int n = AvgEngine.OnBarClosed(c, p, lvl + 8 * 0.25, lvl - 2 * 0.25, lvl + 4 * 0.25, idx);
        T.CheckInt(n, 1, "reclaim bar fires exactly one level");
        T.CheckInt(idx[0], 0, "and it is level 0");
        // the same reclaim again must not re-fire
        n = AvgEngine.OnBarClosed(c, p, lvl + 8 * 0.25, lvl - 2 * 0.25, lvl + 4 * 0.25, idx);
        T.CheckInt(n, 0, "a fired level stays fired");

        // ConfirmBars = 2: touch, one close back (not enough), a close below resets, two closes fire.
        AvgConfig c2;
        var p2 = ArmedNq(out c2);
        c2.ConfirmBars = 2;
        double l2 = p2.LevelPx[0];
        T.CheckInt(AvgEngine.OnBarClosed(c2, p2, l2 + 2 * 0.25, l2 - 0.25, l2 + 0.25, idx), 0, "first close-back only counts");
        T.CheckInt(AvgEngine.OnBarClosed(c2, p2, l2 + 0.25, l2 - 0.25, l2 - 0.25, idx), 0, "close back below resets");
        T.CheckInt(AvgEngine.OnBarClosed(c2, p2, l2 + 0.25, l2, l2 + 0.25, idx), 0, "counter restarts at one");
        T.CheckInt(AvgEngine.OnBarClosed(c2, p2, l2 + 0.25, l2, l2 + 0.25, idx), 1, "second consecutive close fires");

        // dead levels never fire
        AvgConfig c3;
        var p3 = ArmedNq(out c3);
        p3.Dead[0] = true;
        T.CheckInt(AvgEngine.OnBarClosed(c3, p3, lvl + 8 * 0.25, lvl - 2 * 0.25, lvl + 4 * 0.25, idx), 0, "dead level never fires");
    }

    private static void VolAbortIsOneWay()
    {
        AvgConfig c;
        var p = ArmedNq(out c);            // EntryAtr = 12.0, mult 2.0
        T.Check(!AvgEngine.VolAbort(c, p, 20.0), "below threshold: no abort");
        T.Check(AvgEngine.VolAbort(c, p, 25.0), "above threshold: aborts (transition true)");
        T.Check(p.AddsAborted, "latch set");
        T.Check(!AvgEngine.VolAbort(c, p, 25.0), "second call is not a transition");
        var idx = new int[AvgConfig.MAX_LEVELS];
        double lvl = p.LevelPx[0];
        T.CheckInt(AvgEngine.OnBarClosed(c, p, lvl + 0.25, lvl - 0.25, lvl + 0.25, idx), 0, "aborted plan fires nothing");
    }

    private static void SerialiseIsOneInvariantJsonLine()
    {
        var r = new AvgTradeLog();
        r.EntryTs = new DateTime(2026, 8, 19, 18, 5, 30);
        r.Dir = 1; r.Engine = "Cloud"; r.EntryPx = 20000.25; r.EntryQty = 1;
        r.DTicks = 12; r.STicks = 8; r.Levels = 2;
        r.LArm = 600.0; r.LEff = 521.2; r.G = 200.0; r.StopPx = 19992.00;
        r.SpacingSource = "atr";
        r.Fills.Add(new AvgFillRec { Ts = r.EntryTs, Level = -1, PlannedPx = 20000.25, FillPx = 20000.25, Qty = 1 });
        r.Fills.Add(new AvgFillRec { Ts = r.EntryTs.AddMinutes(3), Level = 0, PlannedPx = 19997.25, FillPx = 19998.00, Qty = 1 });
        r.Bars.Add(new AvgBarRec { Ts = r.EntryTs, High = 20001.0, Low = 19999.5, Close = 20000.5 });
        r.Outcome = "tp"; r.Pnl = 200.0; r.MinUnrealized = -180.5;

        string line = AvgLog.Serialise(r);
        T.Check(!line.Contains("\n"), "one line");
        T.Check(line.Contains("\"outcome\":\"tp\""), "outcome serialised");
        T.Check(line.Contains("\"minUnrealized\":-180.5"), "invariant-culture numbers (no comma decimals)");
        T.Check(line.Contains("\"fills\":[") && line.Contains("\"level\":-1"), "entry fill rides as level -1");
        T.Check(line.Contains("\"bars\":[[\"2026-08-19T18:05:30\",20001,19999.5,20000.5]]"), "bar path is a compact array");
        T.Check(line.Contains("\"dTicks\":12") && line.Contains("\"lEff\":521.2"), "solved geometry serialised");
    }
}
