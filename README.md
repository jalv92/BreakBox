<h1 align="center">BreakBox</h1>

<p align="center">
  <b>A NinjaTrader 8 breakout strategy built around a range "box" and a directional cloud filter.</b><br>
  It also carries a sim-only laboratory that measures — never assumes — whether confirmation-gated averaging-down beats the null of EV = 0.
</p>

<p align="center">
  <a href="#how-it-works">How it works</a> ·
  <a href="#averaging-lab-sim-only">Averaging lab</a> ·
  <a href="#status-and-limits">Status and limits</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-research-orange?style=flat-square">
  <img src="https://img.shields.io/badge/box%20edge-none%20(439%20sessions)-red?style=flat-square">
  <img src="https://img.shields.io/badge/platform-NinjaTrader%208-blue?style=flat-square">
  <img src="https://img.shields.io/badge/language-C%23-purple?style=flat-square">
</p>

<!-- Needed in docs/assets/: hero.png (chart with box, cloud, and stop/TP levels drawn, full window). -->
<img src="docs/assets/hero.png" alt="BreakBox chart with the box, cloud filter, and bracket levels drawn" width="100%">

---

## How it works

BreakBox v2 (branch `feat/v2-cloud-and-box`) marks a range "box" on the chart, filters
direction with a cloud engine, and enters on a break of the box with a swing-based
bracket (stop + tiered take-profits). See `docs/superpowers/plans/2026-08-16-breakbox-v2-PLAN.md`
and `docs/superpowers/specs/2026-08-16-breakbox-v2-design.md` for the full design.

## Averaging lab (SIM-ONLY)

An averaging-down laboratory behind the `AveragingEnabled` parameter (default OFF).
It refuses to arm on any account whose name does not start with `Sim` or `Playback`.
Spec and pre-registered kill criteria: `docs/superpowers/specs/2026-08-19-averaging-lab-design.md`.

**Honest use.** Averaging-down cannot create edge (a bounded add schedule has EV = 0
under a driftless price); the planned loss cap is a mode, not a maximum; and the
payoff shape is the one that breaches most prop firms on unrealized drawdown. The
lab exists to MEASURE whether confirmation-gated adds beat that null. Its product is
`<UserDataDir>/BreakBox/averaging_lab_log.jsonl` — one JSON line per armed trade with
the solved geometry, every fill, the bar path, and the minimum open P&L. Inside `fills`,
`level` is `-1` for the entry, `0`/`1` for an add, and `-2` for an exit execution; on a
`-2` row `plannedPx` carries the live average basis at that moment, which is what makes
the competitor arm computable from the file alone. No promotion
decision of any kind before the pre-registered sample (~680 trades, spec §8).

**Where the per-trade budget lives.** Set `AveragingBudgetDollars` ($, in "08. Averaging
lab") to fix it directly, or leave it at 0 to derive it as `AveragingBudgetFraction x
DailyLossLimit` (DailyLossLimit lives in "06. Session"). Either way it is capped by what
remains of today's daily loss limit — one trade can never out-risk the day.

**Before you run it — the shipped defaults do not arm on NQ.** `BaseQuantity = 3` makes the
stack 5 contracts, and the TP floor then needs `G >= $171.20` while the arming budget
(`0.5 x DailyLossLimit` = $225) cannot host a 5-lot grid: every trade refuses with
`target_too_small_for_stack`. Run the lab with `BaseQuantity = 1`, or raise
`AveragingTargetProfitDollars` and `DailyLossLimit` together. On MNQ also set
`AveragingCommissionRt` to the MNQ round-turn (about $1.34) — the default is the NQ one.
`AveragingMaxAdds` is a 1..32 dial, not a clamp: deep grids at the default budget solve to
~1-tick spacing on MNQ, so raise `DailyLossLimit`/`AveragingBudgetFraction` or lower
`AveragingStopBufferTicks` for a usable deep grid.

**Playback verification checklist (run once after any change to the module):**
1. Live-account guard: enable on a non-Sim account name → one loud print, module off, normal bracket runs.
2. Arm print shows solved geometry (d, s, levels, LArm, LEff, stop, tp) and `not armed — <why>` prints on refusals (set G=50 to see `target_too_small_for_stack`).
3. Dip through level 1 + close back above → one market add, stop resizes to the new quantity, TP tightens; the `budget ratchet` note appears when the add fills worse than its level.
4. Straight-line adverse move → NO adds; stop takes out the whole (small) position.
5. Vol spike (or lower `AveragingVolAbortMult` to 1.0) → `vol abort` print, remaining levels drawn dark red, position and stop untouched.
6. Trade closes → `averaging_lab_log.jsonl` gains exactly one line; `outcome`, `fills`, `minUnrealized` match what the chart showed.
7. Rewind Playback mid-trade → no stale prints, next session arms cleanly.
8. On one trade that actually added, check the JSONL `pnl` against NT8's own trade P&L for
   that trade — they must match. The stack is priced off its running average, not off the
   entry, and this is the only check that catches that arithmetic drifting.
9. Reading `minUnrealized`: it samples bar highs/lows from entry to exit, so it does NOT
   include the exit bar's intrabar excursion. Treat it as a floor on the drawdown, not the
   drawdown.
10. **Runner stays protected (base bracket, module OFF too).** On a 2-lot trade, after TP1
    fills the output must show the tier resize/breakeven print AND the Orders tab must show a
    WORKING 1-lot stop. `stop cancelled by hand` must NOT appear — nobody touched anything.
11. **A hand drag is not a hand pull.** Drag the stop in Chart Trader mid-trade: protection
    must survive, either adopted at the new price or re-covered by the strategy. A silent
    unprotected runner is a failure of this check even if nothing prints.
12. **Partial averaging-TP fills re-cover the stop.** Run one armed trade with a large enough
    stack that the whole-stack TP fills in pieces (a thin moment helps). Each partial must
    print `avgtp:resize` and the Orders tab must show a working stop at the reduced quantity —
    this branch has no automated coverage; this check is its only verification.

## Status and limits

- **Core box/cloud strategy: research, unvalidated.** The box measured no edge
  across 439 sessions in the prior validation pass — see memory `breakbox-project`
  for the full history before promoting anything from this repo to live.
- **Averaging lab: sim/Playback only by construction**, gated on account name.
  It has not accrued the pre-registered sample yet, so it carries no verdict.

## License

Private research repository. Not licensed for redistribution.
