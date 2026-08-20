# Averaging Lab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sim-only averaging-down module (pure `AveragingEngineCore.cs` + BreakBoxStrategy wiring + JSONL telemetry) whose telemetry, not its P&L, is the product.

**Architecture:** A pure decision engine (envelope solver, budget-ratchet stop/TP recomputation, confirmation state machine) in a new order-agnostic `.cs`, composed by BreakBoxStrategy exactly the way it already composes `BbExits`. When armed, it replaces the 3-tier bracket with one whole-stack stop + one dynamic TP; adds are market orders fired on bar-close confirmation. Every armed trade writes one JSONL record with the full bar path so counterfactuals compute offline.

**Tech Stack:** C# 7.3, NT8 managed approach (`Exit*` live-until-cancelled, no `Set*`), `dotnet` net8.0 assert runner, `nt8c` via `scripts/check.sh`.

**Spec:** `docs/superpowers/specs/2026-08-19-averaging-lab-design.md` — read it first; every constant below traces to it.

## Global Constraints

- Pure file rules (same as BreakBoxExits.cs): ZERO `using NinjaTrader.*`, namespace `BreakBoxCore`, C# 7.3, no `System.Text.Json` (hand-rolled JSONL like `BbHistory`), never touches an Order.
- Compile gate is `scripts/check.sh` (pure assert suite + all NT8 files concatenated through `nt8c`). The workspace's per-file `nt8c` PostToolUse hook reports FALSE CS0246 positives on every cross-file type in this repo — ignore the hook, trust `check.sh`.
- All code, comments, prints, and docs in English.
- Never derive tick math from hard-coded $5: everything goes through `TickSize` / `Instrument.MasterInstrument.PointValue` / the `TickValue` config field (spec: same L gives d=12 ticks on NQ, 192 on MNQ).
- Order-event race rule: every tracker/flag is written BEFORE the submit that makes it true.
- Sim-only: at `State.Realtime` the module refuses to arm unless `Account.Name` starts with `Sim` or `Playback`. No override dial.
- When the module refuses to arm (any reason), the trade runs the NORMAL tier bracket and the reason prints — a refusal is never a silent no-op and never a blocked trade.
- Commit after each task on branch `feat/averaging-lab`.

---

### Task 1: Pure engine — types + `Arm` envelope solver + test wiring

**Files:**
- Create: `ninjascript/AveragingEngineCore.cs`
- Create: `tests/AveragingTests.cs`
- Modify: `tests/BreakBox.Tests.csproj` (add Compile Include)
- Modify: `tests/Program.cs` (call `AveragingTests.Run()`)
- Modify: `scripts/check.sh` (add `AveragingEngineCore` to `FILES`, before `BreakBoxStrategy`)

**Interfaces:**
- Produces: `AvgConfig` (all dials), `AvgPlan` (armed state), `AvgEngine.Arm(AvgConfig cfg, int dir, double entryPx, int entryQty, double dStructuralTicks, double entryAtr) -> AvgPlan`, `AvgEngine.WorstCaseLoss(AvgConfig cfg, AvgPlan plan) -> double` (price-loss dollars at the planned stop, all levels filled at plan prices, excl. commissions), `AvgEngine.CeilToTick(double px, double tick)` / `FloorToTick`.
- `AvgPlan.Armed == false` ⇒ `RefusedWhy` names the binding constraint: `"budget_below_costs"`, `"budget_too_small_for_grid"`, `"target_too_small_for_stack"`, `"bad_inputs"`.

- [ ] **Step 1: Write the failing tests**

Create `tests/AveragingTests.cs`:

```csharp
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
```

- [ ] **Step 2: Wire the runner and verify the tests fail**

In `tests/BreakBox.Tests.csproj`, inside the `<ItemGroup>`:

```xml
    <Compile Include="../ninjascript/AveragingEngineCore.cs" Condition="Exists('../ninjascript/AveragingEngineCore.cs')" />
```

In `tests/Program.cs`, after `HistoryTests.Run();`:

```csharp
        AveragingTests.Run();
```

In `scripts/check.sh`, change the `FILES=` line to:

```bash
FILES=(BreakBoxTypes BreakBoxCloud BreakBoxCore BreakBoxExits BreakBoxHistory AveragingEngineCore BreakBoxStrategy BreakBoxPanel BreakBoxVision)
```

Run: `dotnet run --project tests`
Expected: compile FAILURE (`AvgConfig` not defined) — that is the failing state.

- [ ] **Step 3: Write the pure engine**

Create `ninjascript/AveragingEngineCore.cs`:

```csharp
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
    }
}
```

- [ ] **Step 4: Run the tests and the compile gate**

Run: `dotnet run --project tests` — Expected: `ALL PASS`, including the new `Averaging: envelope solver` section.
Run: `scripts/check.sh` — Expected: both halves green (`AveragingEngineCore` is now in the concatenation).

- [ ] **Step 5: Commit**

```bash
git add ninjascript/AveragingEngineCore.cs tests/AveragingTests.cs tests/BreakBox.Tests.csproj tests/Program.cs scripts/check.sh
git commit -m "feat(averaging): pure envelope solver with refusal-first Arm"
```

---

### Task 2: Pure engine — `OnFill` budget ratchet, confirmation machine, vol abort

**Files:**
- Modify: `ninjascript/AveragingEngineCore.cs` (extend `AvgEngine`)
- Modify: `tests/AveragingTests.cs`

**Interfaces:**
- Produces: `AvgEngine.OnFill(AvgConfig cfg, AvgPlan plan, double avgPx, int qty) -> AvgUpdate` — recomputes the live stop (one-way ratchet toward price, spends at most `LEff` from the ACTUAL average) and the whole-stack TP `avg + dir*(G + c*qty)*tick/(v*qty)`, and marks dead any level at or beyond `stop + 1 tick`. Called on the ENTRY fill too (single code path seeds the initial TP).
- Produces: `AvgEngine.OnBarClosed(AvgConfig cfg, AvgPlan plan, double barHigh, double barLow, double barClose, int[] fireIdx) -> int` — confirmation machine; returns how many levels fire this bar (indices in `fireIdx`), setting `Fired` inside. A level fires at most once, never when `Dead` or `AddsAborted`.
- Produces: `AvgEngine.VolAbort(AvgConfig cfg, AvgPlan plan, double atrNow) -> bool` — one-way latch; true only on the transition.

- [ ] **Step 1: Write the failing tests**

Append to `AveragingTests.Run()`:

```csharp
        T.Section("Averaging: fill ratchet + confirmation");
        RatchetSpendsTheBudgetFromTheRealAverage();
        TpHoldsConstantNetDollars();
        StraightLineNeverConfirms();
        DipAndReclaimFiresOnce();
        VolAbortIsOneWay();
```

And the test bodies:

```csharp
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
```

- [ ] **Step 2: Run to verify the new tests fail**

Run: `dotnet run --project tests`
Expected: compile FAILURE (`OnFill` not defined).

- [ ] **Step 3: Implement `OnFill`, `OnBarClosed`, `VolAbort` in `AvgEngine`**

```csharp
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
```

- [ ] **Step 4: Run tests and gate**

Run: `dotnet run --project tests` — Expected: `ALL PASS`.
Run: `scripts/check.sh` — Expected: green.

- [ ] **Step 5: Commit**

```bash
git add ninjascript/AveragingEngineCore.cs tests/AveragingTests.cs
git commit -m "feat(averaging): budget-ratchet OnFill, confirmation machine, one-way vol abort"
```

---

### Task 3: Pure telemetry — `AvgTradeLog` + hand-rolled JSONL

**Files:**
- Modify: `ninjascript/AveragingEngineCore.cs` (append types + `AvgLog`)
- Modify: `tests/AveragingTests.cs`

**Interfaces:**
- Produces: `AvgFillRec { DateTime Ts; int Level; double PlannedPx, FillPx; int Qty }` (Level −1 = the entry), `AvgBarRec { DateTime Ts; double High, Low, Close }`, `AvgTradeLog` (see fields in Step 3), `AvgLog.Serialise(AvgTradeLog r) -> string` (one JSON line, invariant culture), `AvgTradeLog.MAX_BARS = 2000`.

- [ ] **Step 1: Write the failing test**

```csharp
        T.Section("Averaging: telemetry");
        SerialiseIsOneInvariantJsonLine();
```

```csharp
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
```

- [ ] **Step 2: Run to verify failure** — `dotnet run --project tests` → compile FAILURE (`AvgTradeLog` not defined).

- [ ] **Step 3: Implement (append inside `namespace BreakBoxCore`)**

```csharp
    public struct AvgFillRec
    {
        public DateTime Ts;
        public int Level;                     // -1 = the entry fill
        public double PlannedPx, FillPx;
        public int Qty;
    }

    public struct AvgBarRec
    {
        public DateTime Ts;
        public double High, Low, Close;
    }

    // One armed trade, everything the offline analysis needs: the solved
    // geometry, every fill, the outcome, the prop-firm axis (min unrealized),
    // and the compact bar path that makes ANY counterfactual — including
    // "flat q0 with the host's own 3-tier bracket" — computable offline
    // without re-running Playback.
    public sealed class AvgTradeLog
    {
        public DateTime EntryTs;
        public int Dir;
        public string Engine = "";
        public double EntryPx;
        public int EntryQty;
        public int DTicks, STicks, Levels;
        public double LArm, LEff, G, StopPx;
        public string SpacingSource = "";
        public readonly List<AvgFillRec> Fills = new List<AvgFillRec>();
        public readonly List<AvgBarRec> Bars = new List<AvgBarRec>();
        public bool AddsAborted;
        public string AbortWhy = "";
        public string Outcome = "";           // tp | stop | session_flatten | other
        public double Pnl;                    // currency, same basis as the trade journal
        public double MinUnrealized;          // most negative open P&L seen, bar lows/highs
        public bool BarsCapped;
        public const int MAX_BARS = 2000;
    }

    public static class AvgLog
    {
        private static string N(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        private static string Ts(DateTime t) { return t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture); }
        private static string S(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        public static string Serialise(AvgTradeLog r)
        {
            var b = new StringBuilder(4096);
            b.Append("{\"entryTs\":").Append(S(Ts(r.EntryTs)))
             .Append(",\"dir\":").Append(r.Dir)
             .Append(",\"engine\":").Append(S(r.Engine))
             .Append(",\"entryPx\":").Append(N(r.EntryPx))
             .Append(",\"entryQty\":").Append(r.EntryQty)
             .Append(",\"dTicks\":").Append(r.DTicks)
             .Append(",\"sTicks\":").Append(r.STicks)
             .Append(",\"levels\":").Append(r.Levels)
             .Append(",\"lArm\":").Append(N(r.LArm))
             .Append(",\"lEff\":").Append(N(r.LEff))
             .Append(",\"g\":").Append(N(r.G))
             .Append(",\"stopPx\":").Append(N(r.StopPx))
             .Append(",\"spacingSource\":").Append(S(r.SpacingSource))
             .Append(",\"addsAborted\":").Append(r.AddsAborted ? "true" : "false")
             .Append(",\"abortWhy\":").Append(S(r.AbortWhy))
             .Append(",\"outcome\":").Append(S(r.Outcome))
             .Append(",\"pnl\":").Append(N(r.Pnl))
             .Append(",\"minUnrealized\":").Append(N(r.MinUnrealized))
             .Append(",\"barsCapped\":").Append(r.BarsCapped ? "true" : "false");

            b.Append(",\"fills\":[");
            for (int i = 0; i < r.Fills.Count; i++)
            {
                var f = r.Fills[i];
                if (i > 0) b.Append(',');
                b.Append("{\"ts\":").Append(S(Ts(f.Ts)))
                 .Append(",\"level\":").Append(f.Level)
                 .Append(",\"plannedPx\":").Append(N(f.PlannedPx))
                 .Append(",\"fillPx\":").Append(N(f.FillPx))
                 .Append(",\"qty\":").Append(f.Qty).Append('}');
            }
            b.Append("],\"bars\":[");
            for (int i = 0; i < r.Bars.Count; i++)
            {
                var bar = r.Bars[i];
                if (i > 0) b.Append(',');
                b.Append('[').Append(S(Ts(bar.Ts))).Append(',')
                 .Append(N(bar.High)).Append(',').Append(N(bar.Low)).Append(',')
                 .Append(N(bar.Close)).Append(']');
            }
            b.Append("]}");
            return b.ToString();
        }
    }
```

- [ ] **Step 4: Run tests and gate** — `dotnet run --project tests` → `ALL PASS`; `scripts/check.sh` → green.

- [ ] **Step 5: Commit**

```bash
git add ninjascript/AveragingEngineCore.cs tests/AveragingTests.cs
git commit -m "feat(averaging): trade telemetry record with hand-rolled JSONL"
```

---

### Task 4: Shell — parameters, Configure, sim guard, arm decision

**Files:**
- Modify: `ninjascript/BreakBoxStrategy.cs` — SetDefaults (~line 316), Configure (~line 317-322), fields (~line 200), Parameters region (before `#endregion` at the file's end), `OpenBracket` (line 1102)

**Interfaces:**
- Consumes: `AvgEngine.Arm`, `AvgEngine.OnFill`, `AvgPlan`, `AvgConfig` (Task 1-2).
- Produces (used by Tasks 5-6): fields `_avgCfg` (AvgConfig), `_avgPlan` (AvgPlan, null when not armed), `_avgArmed` (bool), `_avgQty` (int), `_avgAvgPx` (double), `_avgTpOrder` (Order), `_avgTpChangePending` (bool), `_avgTpSentAt` (DateTime), `_avgLastTpSent` (double), `_avgTpLastQty` (int), `_avgRec` (AvgTradeLog), `_avgLogPath` (string), `_avgSimBlockPrinted` (bool), `_avgFireIdx` (int[]); const signals `SigAdd1 = "BB_Add1"`, `SigAdd2 = "BB_Add2"`, `SigAvgTp = "BB_AvgTp"`; methods `TryArmAveraging(double fillPx, int qty, out string why) -> AvgPlan`, `OpenAveragingBracket(double fillPx, int qty, AvgPlan plan)`, `SubmitAvgTp(double px, string why)`.
- New NinjaScriptProperties: `AveragingEnabled` (bool), `AveragingMaxAdds` (int 1..2), `AveragingAddQty` (int ≥1), `AveragingBudgetFraction` (double 0.05..1), `AveragingTargetProfitDollars` (double >0), `AveragingStopBufferTicks` (int ≥1), `AveragingSpacingSource` (enum Auto|BoxHeight|AtrMult), `AveragingSpacingAtrMult` (double >0), `AveragingConfirmBars` (int 1..5), `AveragingVolAbortMult` (double ≥1), `AveragingNoAddsFinalMinutes` (int ≥0), `AveragingCommissionRt` (double ≥0), `AveragingSlippageReserveTicks` (int ≥0).

- [ ] **Step 1: Signals and fields**

After line 77 (`SigFlatten`) add:

```csharp
        // Averaging lab (SIM-ONLY). Distinct names so OnOrderUpdate can fork:
        // a rejected add is survivable, a rejected protective order is not.
        private const string SigAdd1 = "BB_Add1";
        private const string SigAdd2 = "BB_Add2";
        private const string SigAvgTp = "BB_AvgTp";
```

At the end of the `#region Fields` (before line 204 `#endregion`) add:

```csharp
        // Averaging lab state. _avgQty/_avgAvgPx are OUR fill-accumulated
        // position: inside an execution event NT8 has not necessarily updated
        // Position yet ([[nt8-order-event-race]]), so the recompute reads these,
        // never Position directly.
        private AvgConfig _avgCfg;
        private AvgPlan _avgPlan;
        private bool _avgArmed;
        private int _avgQty;
        private double _avgAvgPx;
        private Order _avgTpOrder;
        private volatile bool _avgTpChangePending;
        private DateTime _avgTpSentAt = DateTime.MinValue;
        private double _avgLastTpSent = double.NaN;
        private int _avgTpLastQty;
        private AvgTradeLog _avgRec;
        private string _avgLogPath = "";
        private bool _avgSimBlockPrinted;
        private readonly int[] _avgFireIdx = new int[AvgConfig.MAX_LEVELS];
```

- [ ] **Step 2: Defaults, Configure, log path**

In `SetDefaults`, after the `// ---- Visuals` block (line ~315) add:

```csharp
                // ---- Averaging lab (SIM-ONLY). Defaults per the 2026-08-19 spec.
                // G defaults to 150, not 100: the TP floor needs G >= QN*(8*v - c),
                // which is $102.72 on NQ at q0=1, N=2 — a $100 default would
                // refuse to arm out of the box.
                AveragingEnabled = false;
                AveragingMaxAdds = 2;
                AveragingAddQty = 1;
                AveragingBudgetFraction = 0.5;
                AveragingTargetProfitDollars = 150;
                AveragingStopBufferTicks = 8;
                AveragingSpacingSource = AvgSpacingSource.Auto;
                AveragingSpacingAtrMult = 1.0;
                AveragingConfirmBars = 1;
                AveragingVolAbortMult = 2.0;
                AveragingNoAddsFinalMinutes = 15;
                AveragingCommissionRt = 5.76;
                AveragingSlippageReserveTicks = 2;
```

In `State.Configure` (line ~317-322), after the existing three allocations add:

```csharp
                // Under EntryHandling.AllEntries NT8 SILENTLY ignores any Enter*
                // beyond this budget — without this line every add is discarded
                // and the module looks armed while doing nothing. Set here, not
                // in SetDefaults: property values are settled by Configure.
                EntriesPerDirection = AveragingEnabled ? AveragingMaxAdds + 1 : 1;
```

In `State.DataLoaded`, after `OpenHistory();` add:

```csharp
                _avgLogPath = System.IO.Path.Combine(
                    System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "BreakBox"),
                    "averaging_lab_log.jsonl");
```

- [ ] **Step 3: Arm decision + averaging bracket open**

Replace the head of `OpenBracket` (line 1102) so it branches FIRST:

```csharp
        private void OpenBracket(double fillPx, int qty)
        {
            if (AveragingEnabled)
            {
                string why;
                AvgPlan plan = TryArmAveraging(fillPx, qty, out why);
                if (plan != null)
                {
                    OpenAveragingBracket(fillPx, qty, plan);
                    return;
                }
                // A refusal is never silent and never blocks the trade.
                Print("BreakBox AVG: not armed — " + why + " — trade runs the normal bracket");
            }
            // ... existing body unchanged ...
```

Then add the two new methods right after `OpenBracket`:

```csharp
        // Every reason NOT to average, checked in cheap-to-expensive order.
        // Returns null with `why` set, or an armed plan.
        private AvgPlan TryArmAveraging(double fillPx, int qty, out string why)
        {
            // SIM-ONLY guard (spec §4): live accounts never arm, no override.
            if (State == State.Realtime && Account != null
                && !Account.Name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)
                && !Account.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase))
            {
                if (!_avgSimBlockPrinted)
                {
                    _avgSimBlockPrinted = true;
                    Print("BreakBox AVG: account '" + Account.Name
                          + "' is not Sim/Playback — averaging lab is SIM-ONLY and stays OFF");
                }
                why = "live_account";
                return null;
            }

            // No arming inside the pre-close cutoff: a dug grid meeting the
            // session flatten is a certain -L on the whole stack.
            int secs = Time[0].Hour * 3600 + Time[0].Minute * 60 + Time[0].Second;
            if (secs >= BbMath.HhmmToSecs(FlattenHhmm) - AveragingNoAddsFinalMinutes * 60)
            {
                why = "inside_close_cutoff";
                return null;
            }

            // The trade's slice of what is LEFT of today's budget — a later
            // trade in a losing day gets a smaller grid automatically.
            double dayLossSoFar = _dayPnl < 0 ? -_dayPnl : 0.0;
            double lArm = Math.Min(AveragingBudgetFraction * DailyLossLimit,
                                   DailyLossLimit - dayLossSoFar);
            if (lArm <= 0.0)
            {
                why = "no_day_budget_left";
                return null;
            }

            if (!_atr.IsWarm || _atr.Value <= 0.0)
            {
                why = "atr_cold";
                return null;
            }

            // Structural spacing candidate (Arm takes the min with the budget d,
            // so structure only ever compresses the grid).
            var box = _engState != null ? _engState.Box : null;
            bool boxUsable = box != null && box.High > box.Low;
            bool useBox;
            switch (AveragingSpacingSource)
            {
                case AvgSpacingSource.BoxHeight:
                    if (!boxUsable)
                    {
                        why = "no_box_for_spacing";
                        return null;
                    }
                    useBox = true;
                    break;
                case AvgSpacingSource.AtrMult:
                    useBox = false;
                    break;
                default: // Auto
                    useBox = _owningEngine == BbEntryEngine.Break && boxUsable;
                    break;
            }
            double dStructTicks = useBox
                ? (box.High - box.Low) / TickSize
                : AveragingSpacingAtrMult * _atr.Value / TickSize;
            string spacing = useBox ? "box" : "atr";

            _avgCfg = new AvgConfig();
            _avgCfg.TickSize = TickSize;
            _avgCfg.TickValue = Instrument.MasterInstrument.PointValue * TickSize;
            _avgCfg.MaxAdds = AveragingMaxAdds;
            _avgCfg.AddQty = AveragingAddQty;
            _avgCfg.BudgetDollars = lArm;
            _avgCfg.TargetDollars = AveragingTargetProfitDollars;
            _avgCfg.StopBufferTicks = AveragingStopBufferTicks;
            _avgCfg.CommissionRt = AveragingCommissionRt;
            _avgCfg.SlippageReserveTicks = AveragingSlippageReserveTicks;
            _avgCfg.ConfirmBars = AveragingConfirmBars;
            _avgCfg.VolAbortMult = AveragingVolAbortMult;

            AvgPlan plan = AvgEngine.Arm(_avgCfg, _dir, fillPx, qty, dStructTicks, _atr.Value);
            if (!plan.Armed)
            {
                why = plan.RefusedWhy;
                return null;
            }

            _avgRec = new AvgTradeLog();
            _avgRec.EntryTs = _entryTime;
            _avgRec.Dir = _dir;
            _avgRec.Engine = _owningEngine.ToString();
            _avgRec.EntryPx = fillPx;
            _avgRec.EntryQty = qty;
            _avgRec.DTicks = plan.DTicks;
            _avgRec.STicks = plan.STicks;
            _avgRec.Levels = plan.Levels;
            _avgRec.LArm = lArm;
            _avgRec.LEff = plan.LEff;
            _avgRec.G = AveragingTargetProfitDollars;
            _avgRec.StopPx = plan.StopPx;
            _avgRec.SpacingSource = spacing;
            _avgRec.Fills.Add(new AvgFillRec { Ts = _entryTime, Level = -1, PlannedPx = fillPx, FillPx = fillPx, Qty = qty });

            why = "";
            return plan;
        }

        // The averaging bracket: ONE stop for the whole stack (SubmitStop, with
        // fromEntrySignal "" while armed) and ONE dynamic TP. The 3-tier bracket
        // is never armed for this trade — _bracket.Tiers stays 0, which keeps
        // SubmitTier, OnTierFill and the breakeven/trail paths inert by
        // construction, while WentFlat/journal/panel read the same fields they
        // always read.
        private void OpenAveragingBracket(double fillPx, int qty, AvgPlan plan)
        {
            _avgArmed = true;
            _avgPlan = plan;
            _avgQty = qty;
            _avgAvgPx = fillPx;
            _avgSimBlockPrinted = false;

            _bracket.Dir = _dir;
            _bracket.EntryPx = fillPx;
            _bracket.AtrRef = _atr.IsWarm ? _atr.Value : 0.0;
            _bracket.Mfe = fillPx;
            _bracket.QtyTotal = qty;
            _bracket.QtyOpen = qty;
            _bracket.BeApplied = true;          // no tiers -> inert, but explicit
            _bracket.TrailArmed = false;
            _bracket.RealizedPts = 0.0;
            _bracket.QtyClosed = 0;
            _bracket.StopCancelled = false;
            _bracket.BarsInTrade = 0;
            _bracket.Tiers = 0;
            for (int i = 0; i < BbExitConfig.MAX_TIERS; i++)
            {
                _bracket.TargetPx[i] = 0.0;
                _bracket.TierFilled[i] = false;
            }
            _bracket.StopPx = plan.StopPx;
            _bracket.InitialStopPx = plan.StopPx;
            _bracket.R = Math.Abs(fillPx - plan.StopPx);
            _bracket.StopWhy = "avg_grid";

            var u = AvgEngine.OnFill(_avgCfg, plan, fillPx, qty);
            _bracket.StopPx = u.StopPx;

            Print(string.Format(CultureInfo.InvariantCulture,
                "BreakBox AVG armed {0} q0={1} d={2}t s={3}t levels={4} LArm={5:0.##} LEff={6:0.##} stop {7} tp {8} ({9})",
                _dir > 0 ? "LONG" : "SHORT", qty, plan.DTicks, plan.STicks, plan.Levels,
                _avgRec.LArm, plan.LEff, _bracket.StopPx, u.TpPx, _avgRec.SpacingSource));

            SubmitStop("avg:init");
            SubmitAvgTp(u.TpPx, "init");
            DrawLevels();
        }

        // The whole-stack dynamic TP. Cancel-replace by reference: the ref is
        // nulled BEFORE the resubmit so our own in-stack Cancelled echo can never
        // be misread ([[latigobreak-project]] commit 575c524 pattern).
        private void SubmitAvgTp(double px, string why)
        {
            if (!_inTrade || !_avgArmed)
                return;
            int qty = _bracket.QtyOpen;
            if (qty < 1)
                return;
            if (!double.IsNaN(_avgLastTpSent) && Math.Abs(px - _avgLastTpSent) < TickSize / 2.0
                && qty == _avgTpLastQty)
                return;

            _avgTpOrder = null;
            _avgTpChangePending = true;
            _avgTpSentAt = DateTime.Now;
            _avgLastTpSent = px;
            _avgTpLastQty = qty;

            if (_dir > 0) ExitLongLimit(0, true, qty, px, SigAvgTp, "");
            else ExitShortLimit(0, true, qty, px, SigAvgTp, "");
        }
```

And in `SubmitStop` (line 1118), the one-line change so the stop covers ALL entry signals while averaging — replace the two submit lines (1147-1148):

```csharp
            string fromSig = _avgArmed ? "" : _entrySig;
            if (_dir > 0) ExitLongStopMarket(0, true, qty, px, SigStop, fromSig);
            else ExitShortStopMarket(0, true, qty, px, SigStop, fromSig);
```

- [ ] **Step 4: Parameters region**

Before the Parameters region's `#endregion`, add (adjust `GroupName` number to the next free one — check the last existing group first):

```csharp
        [NinjaScriptProperty]
        [Display(Name = "Averaging enabled (SIM-ONLY LAB)", Description = "Averaging-down laboratory. Refuses to arm on any non-Sim/Playback account. Module-ON is a DIFFERENT strategy from module-OFF and validates separately.", Order = 1, GroupName = "10. Averaging lab")]
        public bool AveragingEnabled { get; set; }

        [NinjaScriptProperty, Range(1, 2)]
        [Display(Name = "Max adds (N)", Description = "Hard-clamped to 2: all of the benefit is at the first add; depth only levers the tail", Order = 2, GroupName = "10. Averaging lab")]
        public int AveragingMaxAdds { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Add quantity (q)", Order = 3, GroupName = "10. Averaging lab")]
        public int AveragingAddQty { get; set; }

        [NinjaScriptProperty, Range(0.05, 1.0)]
        [Display(Name = "Budget fraction of daily loss", Description = "One trade's slice of DailyLossLimit; also capped by what is left of the day", Order = 4, GroupName = "10. Averaging lab")]
        public double AveragingBudgetFraction { get; set; }

        [NinjaScriptProperty, Range(1.0, 100000.0)]
        [Display(Name = "Target profit G ($, net)", Description = "The trade still exits at this net dollar profit. Floor: G >= stack * (8 ticks * tickValue - commission)", Order = 5, GroupName = "10. Averaging lab")]
        public double AveragingTargetProfitDollars { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Stop buffer s (ticks)", Description = "Below the deepest level; raised to d/2 at arm if smaller", Order = 6, GroupName = "10. Averaging lab")]
        public int AveragingStopBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Spacing source", Description = "Auto = box height when the box engine owns the trade, ATR otherwise; the budget-solved d caps it either way", Order = 7, GroupName = "10. Averaging lab")]
        public AvgSpacingSource AveragingSpacingSource { get; set; }

        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "Spacing ATR mult", Description = "Structural spacing when the source resolves to ATR", Order = 8, GroupName = "10. Averaging lab")]
        public double AveragingSpacingAtrMult { get; set; }

        [NinjaScriptProperty, Range(1, 5)]
        [Display(Name = "Confirm bars", Description = "Bar closes back beyond a touched level before adding — straight-line moves never confirm", Order = 9, GroupName = "10. Averaging lab")]
        public int AveragingConfirmBars { get; set; }

        [NinjaScriptProperty, Range(1.0, 10.0)]
        [Display(Name = "Vol abort mult", Description = "One-way: ATR above this multiple of the entry ATR kills the remaining adds", Order = 10, GroupName = "10. Averaging lab")]
        public double AveragingVolAbortMult { get; set; }

        [NinjaScriptProperty, Range(0, 120)]
        [Display(Name = "No adds final minutes", Description = "No arming or adding this close to FlattenHhmm", Order = 11, GroupName = "10. Averaging lab")]
        public int AveragingNoAddsFinalMinutes { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "Commission RT ($/contract)", Order = 12, GroupName = "10. Averaging lab")]
        public double AveragingCommissionRt { get; set; }

        [NinjaScriptProperty, Range(0, 40)]
        [Display(Name = "Slippage reserve (ticks)", Description = "Reserved out of the budget for the full stack's stop", Order = 13, GroupName = "10. Averaging lab")]
        public int AveragingSlippageReserveTicks { get; set; }
```

- [ ] **Step 5: Compile gate** — Run `scripts/check.sh`. Expected: green (both halves).

- [ ] **Step 6: Commit**

```bash
git add ninjascript/BreakBoxStrategy.cs
git commit -m "feat(averaging): shell arm decision, sim guard, params, averaging bracket open"
```

---

### Task 5: Shell — adds, execution handling, rejection fork, per-bar hook

**Files:**
- Modify: `ninjascript/BreakBoxStrategy.cs` — OnBarUpdate (`if (_inTrade)` block, ~line 674-695), OnExecutionUpdate (~1275-1342), OnOrderUpdate (~1344-1403)

**Interfaces:**
- Consumes: everything Task 4 produced, `AvgEngine.OnBarClosed/VolAbort/OnFill`.
- Produces: `SubmitAdd(int level)`, `OnAddExecution(int level, double price, int qty, DateTime time)`, `AvgOnBar(BbBar bar, int secs)` (used by Task 6's telemetry too).

- [ ] **Step 1: Per-bar hook**

In `OnBarUpdate`, inside `if (_inTrade)` — after the `BbExits.OnBarClose` block (line ~691-694) — add:

```csharp
                if (_avgArmed)
                    AvgOnBar(bar, secs);
```

New method (place after `SubmitAvgTp`):

```csharp
        // The averaging bar loop: TP watchdog, one-way aborts, then the
        // confirmation machine. Adds are MARKET orders fired here, on the main
        // thread at bar close — never from OnMarketData
        // ([[nt8-orders-from-marketdata-thread-crash]]).
        private void AvgOnBar(BbBar bar, int secs)
        {
            // TP watchdog — same shape as the stop's (line ~681).
            if (_avgTpChangePending
                && (DateTime.Now - _avgTpSentAt).TotalSeconds > ExitChangeWatchdogSec)
            {
                Print("BreakBox AVG: TP modify unacknowledged after " + ExitChangeWatchdogSec
                      + "s — resubmitting");
                _avgTpChangePending = false;
                double px = _avgLastTpSent;
                _avgLastTpSent = double.NaN;
                _avgTpLastQty = 0;
                SubmitAvgTp(px, "watchdog");
            }

            // One-way aborts: volatility expansion, then the pre-close cutoff.
            if (AvgEngine.VolAbort(_avgCfg, _avgPlan, _atr.Value))
            {
                _avgRec.AddsAborted = true;
                _avgRec.AbortWhy = "vol_expansion";
                Print(string.Format(CultureInfo.InvariantCulture,
                    "BreakBox AVG: vol abort — ATR {0:0.##} > {1:0.#}x entry ATR {2:0.##}; remaining adds dead, position and stop stay",
                    _atr.Value, AveragingVolAbortMult, _avgPlan.EntryAtr));
            }
            if (!_avgPlan.AddsAborted
                && secs >= BbMath.HhmmToSecs(FlattenHhmm) - AveragingNoAddsFinalMinutes * 60)
            {
                _avgPlan.AddsAborted = true;
                _avgRec.AddsAborted = true;
                _avgRec.AbortWhy = "close_cutoff";
                Print("BreakBox AVG: inside the pre-close cutoff — no more adds this trade");
            }

            int n = AvgEngine.OnBarClosed(_avgCfg, _avgPlan, bar.High, bar.Low, bar.Close, _avgFireIdx);
            for (int i = 0; i < n; i++)
                SubmitAdd(_avgFireIdx[i]);
        }

        private void SubmitAdd(int level)
        {
            int q = AveragingAddQty;
            string sig = level == 0 ? SigAdd1 : SigAdd2;
            Print(string.Format(CultureInfo.InvariantCulture,
                "BreakBox AVG: level {0} confirmed @ {1} — adding {2} at market ({3})",
                level + 1, _avgPlan.LevelPx[level], q, sig));
            if (_dir > 0) EnterLong(0, q, sig);
            else EnterShort(0, q, sig);
        }
```

- [ ] **Step 2: Execution handling for adds**

In `OnExecutionUpdate`: extend the exit-fill accounting condition (line ~1287) to include the averaging TP:

```csharp
            if (_inTrade && (sig == SigStop || sig == SigFlatten || sig == SigAvgTp || IsTierSig(sig)))
                BbExits.AddExitFill(_bracket, price, quantity);
```

After the entry-fill branch (line ~1292-1296) add the add-fill branch — per EXECUTION, not per order state, so partial fills stay correct:

```csharp
            // An add executed. Accumulate OUR average/quantity (Position may be
            // stale in-stack), recompute stop+TP from the REAL numbers, resize.
            if (_avgArmed && _inTrade && (sig == SigAdd1 || sig == SigAdd2) && quantity > 0
                && (execution.Order.OrderState == OrderState.Filled
                    || execution.Order.OrderState == OrderState.PartFilled))
            {
                OnAddExecution(sig == SigAdd1 ? 0 : 1, price, quantity, time);
                return;
            }
```

New method (after `SubmitAdd`):

```csharp
        private void OnAddExecution(int level, double price, int qty, DateTime time)
        {
            _avgAvgPx = (_avgAvgPx * _avgQty + price * qty) / (_avgQty + qty);
            _avgQty += qty;
            _bracket.QtyTotal = _avgQty;
            _bracket.QtyOpen = _avgQty - _bracket.QtyClosed;

            _avgRec.Fills.Add(new AvgFillRec
            {
                Ts = time, Level = level,
                PlannedPx = _avgPlan.LevelPx[level], FillPx = price, Qty = qty
            });

            var u = AvgEngine.OnFill(_avgCfg, _avgPlan, _avgAvgPx, _avgQty);
            _bracket.StopPx = u.StopPx;

            Print(string.Format(CultureInfo.InvariantCulture,
                "BreakBox AVG: add{0} fill {1} @ {2} -> qty {3} avg {4} stop {5}{6} tp {7}",
                level + 1, qty, price, _avgQty, _avgAvgPx, u.StopPx,
                u.StopMoved ? " (budget ratchet)" : "", u.TpPx));

            _lastStopSent = double.NaN;                 // defeat the dedupe, tier-resize idiom
            SubmitStop("avg:add");
            SubmitAvgTp(u.TpPx, "add");
            DrawLevels();
        }
```

- [ ] **Step 3: OnOrderUpdate — refs and the rejection fork**

In the reference-adoption chain (line ~1351-1363), add before the final `else`:

```csharp
            else if (sig == SigAvgTp)
            {
                _avgTpOrder = order;
                if (orderState == OrderState.Working || orderState == OrderState.Accepted)
                    _avgTpChangePending = false;
            }
```

In the `OrderState.Rejected` branch (line ~1367): FIRST add the add-rejection fork (before the protective-leg check), and extend the protective check with the averaging TP:

```csharp
            if (orderState == OrderState.Rejected)
            {
                Print("BreakBox: " + sig + " REJECTED (" + error + ": " + comment + ")");
                // A rejected ADD is survivable: fewer contracts is strictly
                // SAFER under the envelope (monotone loss). Mark the level dead
                // and keep trading — routing this through FlattenAll would turn
                // a routine rejection into a realized loss.
                if (sig == SigAdd1 || sig == SigAdd2)
                {
                    int lvl = sig == SigAdd1 ? 0 : 1;
                    if (_avgPlan != null)
                        _avgPlan.Dead[lvl] = true;
                    Print("BreakBox AVG: add level " + (lvl + 1) + " dead after rejection — position keeps its current size");
                    return;
                }
                if (sig == SigStop || sig == SigTp1 || sig == SigTp2 || sig == SigTp3 || sig == SigAvgTp)
                    FlattenAll("leg_rejected");
                // ... rest of the existing branch unchanged ...
```

- [ ] **Step 4: Compile gate** — Run `scripts/check.sh`. Expected: green.

- [ ] **Step 5: Commit**

```bash
git add ninjascript/BreakBoxStrategy.cs
git commit -m "feat(averaging): confirmation-fired adds, budget resize on fill, rejection fork"
```

---

### Task 6: Shell — telemetry, teardown, draws, honest-use docs

**Files:**
- Modify: `ninjascript/BreakBoxStrategy.cs` — `AvgOnBar` (extend), `WentFlat` (line ~1213), `CancelBracketLegs` (line ~1189), `DrawLevels` region (add averaging draw)
- Modify: `README.md` — new "Averaging lab (SIM-ONLY)" section

**Interfaces:**
- Consumes: `AvgLog.Serialise`, `_avgRec`, `_avgLogPath` (Tasks 3-4).

- [ ] **Step 1: Bar path + min unrealized in `AvgOnBar`**

At the TOP of `AvgOnBar`, before the watchdog:

```csharp
            // Telemetry: the compact bar path (offline counterfactuals) and the
            // prop-firm axis (worst open P&L, intrabar extremes).
            if (_avgRec.Bars.Count < AvgTradeLog.MAX_BARS)
                _avgRec.Bars.Add(new AvgBarRec { Ts = bar.Time, High = bar.High, Low = bar.Low, Close = bar.Close });
            else
                _avgRec.BarsCapped = true;
            double worstPx = _dir > 0 ? bar.Low : bar.High;
            double openPnl = (worstPx - _avgAvgPx) * _dir * _avgQty * Instrument.MasterInstrument.PointValue;
            if (openPnl < _avgRec.MinUnrealized)
                _avgRec.MinUnrealized = openPnl;
```

- [ ] **Step 2: Teardown + JSONL write in `WentFlat`**

In `CancelBracketLegs` (line ~1189) add:

```csharp
            CancelIfLive(_avgTpOrder);
```

In `WentFlat`, right AFTER `AppendHistory(rec);` (line ~1239) — the record's `pnl` is in scope — add:

```csharp
            if (_avgArmed)
            {
                _avgRec.Pnl = pnl;
                _avgRec.Outcome = _exitReason == SigAvgTp ? "tp"
                                : _exitReason == SigStop ? "stop"
                                : (_exitReason == "session_window" || _exitReason == SigFlatten) ? "session_flatten"
                                : "other";
                if (State == State.Realtime)        // the lab logs live sims only; backtests stay off the file
                {
                    try
                    {
                        System.IO.File.AppendAllText(_avgLogPath, AvgLog.Serialise(_avgRec) + Environment.NewLine);
                    }
                    catch (Exception ex)
                    {
                        Print("BreakBox AVG: lab log NOT written (" + ex.Message + ")");
                    }
                }
                Print("BreakBox AVG: trade closed — " + _avgRec.Outcome + ", pnl "
                      + pnl.ToString("C2") + ", min open " + _avgRec.MinUnrealized.ToString("C2")
                      + ", fills " + _avgRec.Fills.Count);
            }
```

And in the reference-nulling block (after `_stopOrder = null;`, line ~1257) add:

```csharp
            _avgTpOrder = null;
            _avgArmed = false;
            _avgPlan = null;
            _avgRec = null;
            _avgQty = 0;
            _avgAvgPx = 0.0;
            _avgTpChangePending = false;
            _avgLastTpSent = double.NaN;
            _avgTpLastQty = 0;
```

- [ ] **Step 3: Visuals**

In `DrawLevels()` (Drawing region, ~line 1554): at its head, add an averaging branch that draws the grid instead of tiers (reuse the existing `DrawTag`/`Tag` helpers and dash-style conventions found in that method — read it before editing):

```csharp
            if (_avgArmed && _avgPlan != null)
            {
                for (int i = 0; i < _avgPlan.Levels; i++)
                {
                    var brush = _avgPlan.Fired[i] ? Brushes.Gray
                              : _avgPlan.Dead[i] ? Brushes.DarkRed : Brushes.Goldenrod;
                    DrawTag(Draw.Line(this, Tag("avgL" + i), false, 8, _avgPlan.LevelPx[i], 0,
                                      _avgPlan.LevelPx[i], brush, DashStyleHelper.Dash, 1));
                }
                DrawTag(Draw.Line(this, Tag("avgTp"), false, 8, _avgLastTpSent, 0, _avgLastTpSent,
                                  Brushes.LimeGreen, DashStyleHelper.Solid, 1));
                DrawTag(Draw.Line(this, Tag("avgStop"), false, 8, _bracket.StopPx, 0, _bracket.StopPx,
                                  Brushes.Red, DashStyleHelper.Solid, 2));
                return;
            }
            // ... existing body unchanged ...
```

(If `DrawLevels` already draws the stop generically, keep that and draw only levels+TP here — read the method first and reuse its idiom; the executor adjusts signatures to whatever `Draw.Line` overload the file already uses.)

- [ ] **Step 4: README — honest-use section + Playback checklist**

Append to `README.md`:

```markdown
## Averaging lab (SIM-ONLY)

An averaging-down laboratory behind the `AveragingEnabled` parameter (default OFF).
It refuses to arm on any account whose name does not start with `Sim` or `Playback`.
Spec and pre-registered kill criteria: `docs/superpowers/specs/2026-08-19-averaging-lab-design.md`.

**Honest use.** Averaging-down cannot create edge (a bounded add schedule has EV = 0
under a driftless price); the planned loss cap is a mode, not a maximum; and the
payoff shape is the one that breaches most prop firms on unrealized drawdown. The
lab exists to MEASURE whether confirmation-gated adds beat that null. Its product is
`<UserDataDir>/BreakBox/averaging_lab_log.jsonl` — one JSON line per armed trade with
the solved geometry, every fill, the bar path, and the minimum open P&L. No promotion
decision of any kind before the pre-registered sample (~680 trades, spec §8).

**Playback verification checklist (run once after any change to the module):**
1. Live-account guard: enable on a non-Sim account name → one loud print, module off, normal bracket runs.
2. Arm print shows solved geometry (d, s, levels, LArm, LEff, stop, tp) and `not armed — <why>` prints on refusals (set G=50 to see `target_too_small_for_stack`).
3. Dip through level 1 + close back above → one market add, stop resizes to the new quantity, TP tightens; the `budget ratchet` note appears when the add fills worse than its level.
4. Straight-line adverse move → NO adds; stop takes out the whole (small) position.
5. Vol spike (or lower `AveragingVolAbortMult` to 1.0) → `vol abort` print, remaining levels drawn dark red, position and stop untouched.
6. Trade closes → `averaging_lab_log.jsonl` gains exactly one line; `outcome`, `fills`, `minUnrealized` match what the chart showed.
7. Rewind Playback mid-trade → no stale prints, next session arms cleanly.
```

- [ ] **Step 5: Compile gate + full test run** — `scripts/check.sh` green, `dotnet run --project tests` ALL PASS.

- [ ] **Step 6: Commit**

```bash
git add ninjascript/BreakBoxStrategy.cs README.md
git commit -m "feat(averaging): JSONL telemetry, teardown, grid visuals, honest-use docs"
```

---

### Task 7: Deploy to NT8 and close out

**Files:**
- Copy: `ninjascript/AveragingEngineCore.cs`, `ninjascript/BreakBoxStrategy.cs` → `/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Strategies/`

- [ ] **Step 1: Final gates**

Run: `scripts/check.sh` && `dotnet run --project tests` — both green.

- [ ] **Step 2: Deploy (house rule: a task is not done until the .cs is in Custom)**

```bash
DEST="/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Strategies"
cp ninjascript/AveragingEngineCore.cs "$DEST/"
cp ninjascript/BreakBoxStrategy.cs "$DEST/"
cmp ninjascript/AveragingEngineCore.cs "$DEST/AveragingEngineCore.cs" && echo OK-core
cmp ninjascript/BreakBoxStrategy.cs "$DEST/BreakBoxStrategy.cs" && echo OK-strategy
# no duplicate basenames between Indicators/ and Strategies/
comm -12 <(ls "$DEST/../Indicators" | sort) <(ls "$DEST" | sort)
```

Expected: two `OK-*` lines, empty `comm` output. (If NT8 already compiled BreakBoxStrategy.cs once, `cmp` can differ by the platform's generated region — then compare normalized prefixes per the house note in `[[breakbox-project]]`.)

- [ ] **Step 3: Commit + push the branch**

```bash
git add -A && git status --short   # expect clean or only intended files
git push -u origin feat/averaging-lab
```

- [ ] **Step 4: Hand to Javier** — F5-compile inside NT8, then the README Playback checklist. The lab accrues `averaging_lab_log.jsonl`; the offline analysis (kill criteria, spec §8) runs on that corpus — it is a separate, later task and is NOT part of this plan.

---

## Self-review notes (kept for the executor)

- Spec §3 "one armed trade at a time" is structural: the host is single-position (`positioned` gate in OnBarUpdate).
- Spec §3 "ATM mode: refuse" is N/A: BreakBox has no ATM path.
- Spec §3 "restart mid-grid → flatten": NT8 starts strategies flat by default; a position adopted from the account cannot reach `OpenBracket` (no entry fill), so no grid ever resumes mid-trade — nothing to build.
- `BbExits.OnBarClose` keeps running while averaging (harmless: `Tiers=0`, `TrailArmed=false` → it only tracks Mfe/BarsInTrade and returns "hold"); do not gate it out.
- The `Set*` family is absent from this strategy — the Exit*-only rule holds by construction.
- Session-close flatten of a dug grid is a LOGGED third outcome (`session_flatten`), not a bug.
