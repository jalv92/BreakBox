# BreakBox — design (v2)

**Status:** built, compiles clean, deployed to NinjaTrader. **Nothing here is validated.**
**Spec:** [`superpowers/specs/2026-08-16-breakbox-v2-design.md`](superpowers/specs/2026-08-16-breakbox-v2-design.md) — the binding authority.
**Plan and execution record:** [`superpowers/plans/`](superpowers/plans/)

> This file supersedes the v1 design, which described a build that **took zero trades**. If you are
> looking for `RequireCloseOutside`, `BbBoxSource`, the Retrace engine or a 240-minute box, they were
> deleted — §1 explains why.

---

## 0. Clean room

Rebuilt from **observed behaviour** measured off screenshots of a competitor product. No
decompiling, no reuse of their names, order prefixes or branding. **This repo stays private.** Our
order signal names are `BB_CloudLong` / `BB_CloudShort` / `BB_BoxLong` / `BB_BoxShort`.

## 1. Why v1 took zero trades

Not a bug — a dimensional error.

```
Box #17:  29867.25 / 29592.25   →  275.00 points   (a 240-MINUTE range)
ATR:      7.85                                      (a 14-BAR average on 30s bars)
275 / 7.85 = 35 ATR             MaxBoxRangeAtr = 6  →  invalid  →  no engine ever runs
```

The box was a wall-clock object, the ATR a bar-count object, and the validity band divided one by
the other. **No tuning fixes that.** Two errors sat on top: the reference panel reads
`Signal ON / Break OFF`, so its trades came from the *signal* engine while v1 built *break* as
primary; and the 4H `PH Low` rectangle it copied is distant context, not the traded object — the
real one is a ~7-bar accumulation drawn in white.

## 2. What v2 is

Two independent signal engines feed one already-tested bracket.

**Cloud** (the primary — the reference's `Signal`): an EMA ribbon defines a **latched** regime; a
pullback that touches the ribbon's far edge mints a consumable **token**; a reclaim bar that passes
five quality gates fires a stop entry beyond its own high. The latch exists because a pullback deep
enough to touch the far edge normally zeroes the *instantaneous* regime — without it the engine
would mint a token and destroy it on the same move, every time, and trade nothing.

**Box** (rebuilt at the right scale): a small accumulation with a real lifecycle — form, seal,
invalidate — whose validity is judged **against the distribution of other boxes**, not against a
bar-count ATR. Arming spends one of a bounded per-edge budget rather than latching a single boolean —
a refusal (cancelled/rejected) refunds its arm because we declined the trade ourselves, but an expiry
still spends one because the market declined a live order, and the whole budget clears on an ordinary
inside close rather than v1's near-impossible latch condition. There is no cross-session seed: a
fresh attach warms up from scratch — `BoxMeanSamples` boxes (20 by default) have to seal before the
engine trades at all, which at the defaults is a lower bound of ~9 bars for the first one and no
fixed ceiling after that (a live box blocks the next candidate until it dies, by a break or by
`BoxMaxAge`). Unmeasured against real data; the panel's own `BOX WARMING — n/N boxes sealed` readout
is the only real clock.

**Bracket** (unchanged from v1, and the one thing v1 got right): a structural stop from five
selectable sources clamped into an ATR band, up to three take-profit tiers priced as R-multiples,
breakeven on the first tier fill, and a chandelier trail after the second.

## 3. Files

| File | Responsibility | Pure? |
|---|---|---|
| `BreakBoxTypes.cs` | bars, swings, ATR, EMA, rounding, `BbScale`, `BbGateReport`, `BbMath` | yes |
| `BreakBoxCloud.cs` | the cloud engine: regime latch, pullback token, gold-candle gates | yes |
| `BreakBoxCore.cs` | the box engine: lifecycle, validity gate, cold start, arming | yes |
| `BreakBoxExits.cs` | the bracket | yes |
| `BreakBoxHistory.cs` | trade record, wire format, equity reduction, log ring | yes |
| `BreakBoxStrategy.cs` | series, orders, session, governor, scaling, arbitration, file I/O | no |
| `BreakBoxPanel.cs` | the vertical panel — partial of the strategy | no |
| `BreakBoxVision.cs` | **indicator**, trades nothing. Deploys to `Custom/Indicators/` | no |

"Pure" means zero `using NinjaTrader.*` — those files compile in a net8 test runner with no NT8
assemblies, which is what lets **435 asserts** run headless.

## 4. The four rules that carry the design

**Every horizon on the parameter surface is in SECONDS**, converted once through `BbScale.Bars`
inside `BuildConfigs()` — not `State.DataLoaded`, because every panel toggle calls `BuildConfigs()`
again. A chart change is a *resolution* change, not a *model* change, and the same defaults run on
15s / 30s / 1m / 2m. The only bar-valued dials left are indicator **periods** (`AtrPeriod`,
`MaPeriod`, `E50Period`, `SwingStrength`), which is the convention every platform uses.

**One live trigger at a time, with an owner.** Cloud is evaluated first; the shell stamps
`_owningEngine` on submit and forwards expiry, rejection and fills **only** to that engine. A
suppressed action is not consumed.

**`canTrade` is a final veto, not an early gate.** Both engines evaluate their full gate chain and
record the deepest rung reached, *then* suppress. Nothing arms and nothing fires with auto-trade
off — but the diagnostic counters see the real distribution, which is what makes the calibration
protocol worth running.

**The panel must not lie.** Both engines publish a `GateLadder`; the panel **derives** its rows from
those arrays and never copies them. v1's panel showed `READY` next to `(out of band)` and never
connected the two, which is how a dead strategy went unnoticed for an hour.

## 5. What the panel shows

300 DIP, docked left, full height. A `WHY NO TRADE` ladder naming the first failing gate and what it
needs versus what it has. A three-line engine log. Controls for both engines, both directions, risk
and stop source. And a **history chart that survives restarts** — cumulative equity over
`today | 20d | 100t`, with every trade tagged by a digest of the 57 dials that change what is
traded, so a parameter change renders as a visible seam instead of silently contaminating the
record. That digest is what makes the chart answer *"is the configuration I am running now
working?"* rather than *"has this ever made money?"*.

## 6. Nothing here is validated

Every default is provisional. The R-multiples are the ones measured off **one** of two observed
configs, and the whole finding was that they are re-optimised per session.

A 439-session measurement on `NQData` established that the **box compression-break carries no
directional information** at any horizon, and that a random entry through the same bracket is
indistinguishable from it. The box engine is built anyway, at the user's explicit direction, at a
scale that measurement never tested. **Do not read its presence as evidence it works.**

The next step is not more code — it is spec §13:

1. **Count only.** Orders disabled, per-gate counters, 5–10 Replay sessions, *then* tune. The
   instrumentation is built and prints at the session close.
2. **Random-entry control** on `NQData` through the identical bracket. Not separable from random →
   does not proceed.
3. **Freeze the measured parameters, tune at most three guesses, pre-register the rest.**

Gates: `[[strategy-profitability-gates]]`, out-of-sample only, **never the vendor's numbers**.

## 7. Verification

```
scripts/check.sh
```

Two halves, both must be clean: the pure files compile with zero NT8 assemblies and 435 asserts run;
then all NT8 files are concatenated into one compilation unit and checked with `nt8c`. `nt8c check`
on a *single* file reports false CS0246 on every cross-file type — that is why the gate concatenates.

Three **fidelity** tests replay prices measured off the reference product to the cent. If one starts
failing, something changed the geometry this project was reverse-engineered from. That is a
stop-work signal, not a test to update.
