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
the competitor arm computable from the file alone. **Every line carries `cfgHash`, and lines with
different values are different experiments — never pool them.** The three breakeven dials are inside
that digest, so a BE-on run and a BE-off run append to the same file and are told apart only by it;
`beApplied:false` on its own is ambiguous between "breakeven was off" and "breakeven was on and never
triggered". No promotion decision of any kind before the pre-registered sample (~680 trades, spec §8).

**Where the per-trade budget lives.** Set `AveragingBudgetDollars` ($, in "08. Averaging
lab") to fix it directly, or leave it at 0 to derive it as `AveragingBudgetFraction x
DailyLossLimit` (DailyLossLimit lives in "06. Session"). Either way it is capped by what
remains of today's daily loss limit — one trade can never out-risk the day.

**Breakeven, and what it means here.** `AveragingBreakevenEnabled` (on by default) is the module's
own breakeven and has nothing to do with `BreakevenOnTp1` in "05. Targets", which stays inert on
averaging trades. Once the bar extreme has covered `AveragingBreakevenPct` (default 50) of the
distance from the position to its take-profit, the whole-stack stop moves to
`average + AveragingBreakevenOffsetTicks` (default 5) in our favour. **Breakeven means the LIVE
AVERAGE, not the entry price** — on an averaging stack the average is the cost basis and the dynamic
TP already hangs off it, so both ends of the measurement are anchored on the same number; measured
from the entry, a dug grid's progress would read negative for most of its life. It fires once,
and it never moves the stop backwards: if the budget ratchet already parked it tighter, breakeven
latches and does nothing. **Firing it disarms the rest of the grid** — every remaining add level sits
below the new stop, and an add below the stop is a fill the envelope never priced, so those levels
die. That is intended: the rescue worked, stop rescuing. The JSONL carries `beApplied`/`bePx`, which
is what separates a BE'd trade from a real `tp`/`stop` in the outcome accounting, plus `beTs` to
locate the intervention inside that trade's bar path. Note that a BE'd exit fills through the stop
order, so it logs as `outcome:"stop"` with a small POSITIVE `pnl` — spec §8 says how to read that,
and why the two BE settings are separate arms split by `cfgHash` rather than by `beApplied`.

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
13. **The timed flatten actually closes the position.** Mid-Playback, with a position open, set
    `Flatten HHMM` ("06. Session") a couple of minutes ahead of the replay clock. The position
    must close and the output must print the `session_window` exit. The flatten is a latch, not
    a one-minute window: it keeps firing until the session open, so a thin tape with no bar
    closing inside that minute can no longer skip it.
14. **Averaging breakeven fires and disarms the grid.** With the module armed, let price run half
    way from the average to the TP. The output must print `BreakBox AVG: BREAKEVEN`, naming the new
    stop, the average it came from, and how many add levels it just killed. The Orders tab must show
    a WORKING stop at `average + 5 ticks` (rounded to the next tick away from the average, so off an
    off-tick average the stop sits a fraction beyond that — correct, not a miscalculation), not at the
    grid bottom, and the trade's line in
    `averaging_lab_log.jsonl` must carry `"beApplied":true` with a matching `bePx`. After it fires,
    a dip back through an add level must add nothing — that is the disarm, not a missed signal.

## Status and limits

- **Core box/cloud strategy: research, unvalidated.** The box measured no edge
  across 439 sessions in the prior validation pass — see memory `breakbox-project`
  for the full history before promoting anything from this repo to live.
- **Averaging lab: sim/Playback only by construction**, gated on account name.
  It has not accrued the pre-registered sample yet, so it carries no verdict.

## License

Private research repository. Not licensed for redistribution.
