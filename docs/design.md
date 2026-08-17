# BreakBox — design

**Status:** built, compiles clean, deployed to NT8. **Zero validation.** Javier
decides in NinjaTrader whether it was worth building.

Provenance and the clean-room boundary: `research/01-target-analysis.md`. Read
that first — it is where every number below comes from.

---

## 1. The model in one paragraph

Build a **box** from a completed period. Trade either its **break** (a stop
order beyond the edge) or the **retrace** back to that edge after a real
extension. Protect the position with a **structural stop** (five selectable
sources, clamped into an ATR sanity band) and take profit in up to **three tiers
priced as R-multiples**, where R = |entry − stop|. Move to breakeven when tier 1
fills; trail the runner after tier 2.

That geometry is not invented. It is what the target's screenshots gave up:
three take-profits landing on exact R-multiples to the cent, on two samples
whose R differed by 2.6× (§4.1 of the research doc). The multiples themselves
differ between the samples, which is why they are three independent dials and
not a doubling rule.

---

## 2. Files

| File | What lives there | NT8? |
|---|---|---|
| `BreakBoxTypes.cs` | Bars, swings, Wilder ATR, EMA, rounding, HHMM/window math | No — pure |
| `BreakBoxCore.cs` | Box construction, the Break engine, the Retrace engine, budgets | No — pure |
| `BreakBoxExits.cs` | Structural stop, tier pricing, the split, breakeven, trail | No — pure |
| `BreakBoxStrategy.cs` | Series, indicators, orders, session, governor, drawing | Yes |
| `BreakBoxPanel.cs` | On-chart WPF panel + session HUD (partial of the strategy) | Yes |

The three pure files carry **every decision** and compile with no NinjaTrader
assemblies on the reference path — which is what lets `tests/` run them headless.
Anything that reads a clock, submits an order or touches a chart is plumbing and
lives in the other two.

---

## 3. The box

Three sources, one selector:

- **PriorPeriod** — the previous HTF slot. Default 240 minutes, anchored to the
  18:00 ET session open, which is where NT8 anchors its own 4H bars. This is the
  cyan `PH High` / `PH Low` box visible on the target's chart.
- **InitialBalance** — the first N minutes after the cash open (default 09:30 +
  60m). The bundled IB bot's object.
- **PriorSession** — the whole previous ETH session.

The box is folded from the primary 1-minute series **inside the engine**, not
from an `AddDataSeries`. One series, no fold indices, and the test runner can
build a box without NT8 in the room.

A box outside `[MinBoxRangeAtr, MaxBoxRangeAtr]` × ATR is marked invalid and
never traded: a 0.2-ATR box is noise whose "break" is one tick of drift, and an
8-ATR one is a trend leg with two arbitrary ends.

## 4. The two entry engines

Independently toggled, exactly like the target's `Signal` / `Break` pair.

**Break.** On a bar closing outside the edge (or touching it, with
`RequireCloseOutside` off), arm a **stop order beyond that bar's extreme** —
not beyond the box edge. On the bar that closes 12 points through the level, a
trigger at edge+1 tick is already deep inside the market. The trigger expires
after `TriggerLifeBars` and dies immediately if price closes back inside.

A **spent latch** stops a sustained break from re-arming every bar for the rest
of the session: once this edge's break is taken, nothing re-arms until price is
back inside the box. Without it, an expired trigger is replaced by a fresh one on
the very next bar, forever.

**Retrace.** Track the excursion beyond the edge. Once it reaches
`ExtensionAtr` × ATR it **qualifies** — including on the bar that opened it, so a
single explosive bar counts. Then, when price comes back to the edge, place a
limit there. The bar that opened the excursion may not fire its own retrace: its
low (on an up-move) is by construction near the edge it just left, so without
that guard every extension bar is also a retrace signal, entering at the edge on
the way *out*.

## 5. The bracket

**Stop.** Five sources — `Candle` (signal bar extreme), `Swing` (last confirmed
pivot), `Ma`, `E50`, `Manual` (fixed ticks) — each offset by `StopBufferTicks`.
A source that is missing, or that sits on the **wrong side of the entry** (a
50-EMA above price on a long is an ordinary market state), falls back to the
manual distance and **says so** in the log: a silent fallback is how a strategy
runs a week on the wrong stop. The distance is then clamped into
`[StopMinAtr, StopMaxAtr]` × ATR — a sanity band, not a re-derivation. Measured
R/ATR on the one legible sample was 1.17.

**Tiers.** Up to three, priced at `Tp1R` / `Tp2R` / `Tp3R` multiples of R from
the **fill** (not the signal price — R is only real risk when measured from the
price we actually got). Forced monotone, floored off the stop's own gap and off
the entry.

**The split.** `floor(x + 0.5)` per tier, last live tier takes the remainder.
On a 7-lot at 50/30 that is **4/2/1 — exactly what the target's chart shows**.
Tiers that cannot get a contract are dropped and the tier count shrinks; three
targets on a 2-lot is a configuration that cannot be filled.

**Breakeven** fires from the **TP1 fill**, not from an R threshold. Those are the
same event only while TP1R happens to equal the threshold, and they diverge
silently the moment either is re-tuned. One-shot and monotone.

**Trail** arms on the TP2 fill: a monotone chandelier off the MFE at
`TrailAtrMult` × ATR, using the **live** ATR (a runner held through a regime
change should be trailed by the regime it is in) while R stays frozen.

## 6. The shell

- **Orders.** Entry is a stop-market or a limit. Exits are one stop covering the
  open quantity plus one limit per live tier, each with its own signal name. No
  `SetStopLoss`/`SetProfitTarget`/`SetTrailStop` anywhere — the managed approach
  ignores `Exit*` while a `Set*` is active, which is what silently reverts a
  bracket dragged by hand.
- **The order-event race.** Every in-flight flag is written **before** its
  submit; every handler clears on the signal *name*. NT8 can deliver the fill
  in-stack before `Enter*` returns.
- **Watchdog.** A stop modify unacknowledged after 5s forgets what it thinks it
  sent and re-submits. Otherwise a swallowed modify leaves the old price live
  forever, because the next candidate compares equal and returns early.
- **A rejected bracket leg flattens.** It does not retry: a naked position is the
  one failure that costs real money.
- **A hand-cancelled stop stays cancelled.** Nothing resurrects that leg.
- **Session.** Hard entry window plus a flatten time, on a clock that wraps at
  midnight (every window in an ETH session can wrap).
- **Governor.** Daily loss limit, daily profit target, per-box and per-day trade
  budgets. Budget is consumed by a **fill**, never by a submit — otherwise
  `MaxTradesPerBox = 1` becomes zero trades on a day of cancelled entries.

## 7. The panel

`UserControlCollection` (not `ChartControl.Children`, which loses its controls
when a strategy is added or removed). Inventory mirrors §4.3 of the research doc:
AUTO-TRADE + status, the two engine toggles, direction gates, risk multiplier
(0.5/1/1.5), the five stop sources, Flatten / BE / Lock Out, Manual Buy / Sell,
ATR readout, window countdown, and the HUD (daily P&L, trade count, W/L, win
rate).

**Threading.** Clicks arrive on the WPF thread, where order calls are illegal,
and `OnMarketData` is equally off-limits (memory `[[nt8-orders-from-marketdata-thread-crash]]`).
Everything that touches an order goes through `TriggerCustomEvent`, NT8's
supported bridge back onto the strategy thread. Toggles that only flip a setting
just write a field and rebuild the config.

**Manual Buy/Sell route through the same bracket machinery.** A manual trade with
no structural stop and no tiers would be a different product wearing the same
panel.

**A hand Lock Out survives the session roll.** A governor lockout does not. The
difference is "I hit my daily loss" versus "I am done for today".

---

## 8. Verification

```
scripts/check.sh
```

Two gates, because no single tool covers both:

1. `dotnet run --project tests` — the three pure files build with zero NT8
   assemblies, and 99 asserts run. Includes three **fidelity** tests that replay
   the exact prices and lot splits measured off the target's screenshots.
2. All five files concatenated into one compilation unit and checked against
   NT8's references. `nt8c check` is per-file and reports CS0246 on every
   cross-file type (VeeSnapCore.cs, in production, fails identically);
   `nt8c build` drowns in ~2100 pre-existing errors from NT8's own `@`-samples.

Current: **99/99 asserts, 0 errors, 0 warnings.**

## 9. What is NOT here

- **Layer 4, the walk-forward validator.** The target's actual moat. Out of scope
  by explicit decision: Javier validates in NT8 by hand.
- **Layer 5, the multi-account cascade.** The high-risk piece. The panel carries
  no account selector; the strategy trades the chart's account.
- **The equity sparkline.** The HUD is text. A SharpDX sparkline is a lot of
  `OnRender` for information the numbers already carry.

## 10. Every default is PROVISIONAL

Nothing here has been validated on a tape. The R-multiples are the ones measured
off **one** of two observed configs, and the entire finding was that they are
re-optimised per session. `TriggerLifeBars`, `ExtensionAtr`, the ATR bands and
the box validity band are engineering judgement, not measurements.

Gates when the time comes: memory `[[strategy-profitability-gates]]`,
out-of-sample only. **Never the vendor's numbers** — their PF 1.69 / 72.9% WR is
an unverified backtest on their own data, and the screenshots are demo material
(§1 of the research doc proves the arithmetic).
