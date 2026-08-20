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
}
