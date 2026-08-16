# BreakBox — Target Analysis & Reverse-Engineering Brief

**Date:** 2026-08-16
**Status:** Research complete, no code written yet
**Source material:** 4 screenshots in `reference/`

---

## 1. What we are actually looking at

The screenshots are not one product. They are **two products from the same vendor**, plus
the vendor's own sales page.

| File | Content | Product | Evidence |
|---|---|---|---|
| `...16.40.50 (3).jpeg` | the storefront sales page | **Product A (the autotrader) — NinjaTrader**, $197/yr, 4.8★ (54) | Page header + "By the vendor" |
| `...16.40.50 (2).jpeg` | Dark chart, MNQ ~26877 | **Product A (the autotrader)** | `the vendor` watermark on dashboard, `A_` order prefix, a funded-account name |
| `...16.40.50.jpeg` | Light chart, NQ ~23713 | **Product B (the signal build)** | `B_` order prefix |
| `...16.40.50 (1).jpeg` | Light chart + dashboard | **Product B (the signal build)** | `B_` prefix, Sim101 account |

`B_` = Product B. Both products are sold from the same the storefront store (`the vendor storefront`),
share the same panel/dashboard/bracket machinery, and differ mainly in the signal engine.
So the two prefixes are the same execution shell wearing two badges.

### Provenance check (the screenshots are internally consistent)

Image (2) and the video thumbnail inside the sales page (3) are **the same live session, minutes apart**:

```
Sales-page thumbnail:  DAILY P&L +$238.50   trades #2..#6   W:5 (83%) L:1
Image (2):             DAILY P&L +$348.50   trades #3..#7   W:6 (86%) L:1
                       #7 MNQ LONG +$110.00  ← the new trade
                       238.50 + 110.00 = 348.50  ✓ exact
```

Per-trade values for #3–#6 match across both. Conclusion: image (2) is **vendor demo
material**, not an independent user's run. Treat every performance number in these
screenshots as marketing, not evidence.

Same check on image (1): shown trades #4..#8 sum to +$1,172.00 while DAILY P&L reads
+$465.50 → trades #1..#3 lost −$706.50 combined. With 5W/3L overall and 4W/1L visible,
#1..#3 must be 1W/2L. Arithmetically coherent — the dashboard is a real running tally,
just of a cherry-picked session.

---

## 2. Clean-room boundary (read before writing code)

We are rebuilding **observed behaviour**, not their code.

**Allowed and what we will do:**
- Read the screenshots, measure the geometry, infer the rules.
- Read their public marketing claims.
- Write 100% original NinjaScript from that specification.

**Not allowed, and we will not do it:**
- Decompiling, disassembling or unpacking their DLL / protected assembly.
- Circumventing their licensing.
- Reusing their names, branding, order-prefixes (`A_`, `B_`) or marketing copy.
- Publishing this repo publicly. **The repo is private** (`jalv92/BreakBox`).

This is clean-room reimplementation from black-box observation, which is the legitimate
form of reverse engineering. The line we do not cross is their binary.

---

## 3. The architecture they advertise (public claims)

From `the vendor.io` and the the storefront listings:

> "Optimise → Validate → Forward Test → Deploy"

- **Optimise** — "Thousands of filter, stop and target combinations, back-tested against
  your recent history", run **separately for Asia, London and New York** session windows.
- **Validate** — "optimises on 70% of the data, then proves the edge on the 30% it never
  saw — re-validated every single session." Overfit candidates are "dropped before they
  trade a cent".
- **Strategy Explorer** — ranked list of surviving configurations; the user can lock one
  in manually instead of full autopilot.
- **Deploy** — "Entries, stops, targets and break-even fired to plan", cascade across
  **up to 10 funded accounts, each with its own risk**, per-account profit target and loss cap.
- **Instruments:** NQ/MNQ, ES/MES, GC/MGC, CL/MCL. NinjaTrader 8 only, no external bridge.

Signal components named (Golden Candle / Product B line):
multi-timeframe confirmation, **RSI**, **MACD**, "Trend Theory", "Volume Bubbles",
market structure (**swings, break lines, trend waves**), institutional/"premium" zones,
higher-timeframe alignment, 3-tier targets, dynamic trailing stop, breakeven on TP hits,
"guardian protection", risk calculator.

Separate bundled product — **the vendor's Initial Balance bot** (NQ/MNQ): maps the first
hour's Initial Balance, waits for a real extension, then **enters the retrace instead of
chasing**. Claimed 2019–2026 backtest: PF 1.69, 72.9% WR (unverified, vendor-supplied).

### The critical finding

**The entry logic is not publicly recoverable.** Every marketing page describes the
*validation methodology* in detail and the *signal* not at all — deliberately. We have the
ingredient list, never the recipe.

This is less of a problem than it sounds, and it reshapes the project (see §7):
their own pitch is that **the signal is not fixed** — it is re-selected per session by the
optimiser out of a combinatorial space. A faithful reimplementation therefore does not
need their exact signal. It needs *a signal library + the optimiser*. The optimiser is the
product. That part is fully described and fully buildable.

---

## 4. Forensic findings from the screenshots

### 4.1 Bracket geometry — SOLVED

This is the highest-confidence finding. The take-profits are **exact R-multiples of the
stop distance**, where R = |entry − stop|.

**Image (1), NQ short, `B_`:**

```
Entry  23713.75
Stop   23729.25   → R = 15.50 pts
TP1    23707.55   = Entry − 6.20   = 0.40 R   (15.50 × 0.40 = 6.20  ✓)
TP2    23701.35   = Entry − 12.40  = 0.80 R   (15.50 × 0.80 = 12.40 ✓)
TP3    23690.50   = Entry − 23.25  = 1.50 R   (15.50 × 1.50 = 23.25 ✓)
```

Three exact hits to the cent. Note TP1/TP2 land on **non-tick prices** (…7.55, …1.35) —
they are computed levels drawn on the chart, then rounded to the tick grid when the order
is submitted (TP2 line 23701.35 → filled 23701.25 ✓).

**Image (2), MNQ long, `A_`:**

```
Entry  26877.00   (fill 26877.25)
Stop   26871.1x   → R ≈ 5.85
TP1    26879.9x   = Entry + 2.925  = 0.50 R
TP2    26882.8x   = Entry + 5.85   = 1.00 R
```

**What this tells us:**
1. TP2 is always exactly **2 × TP1**. Held in both samples.
2. The multiples themselves **differ between the two configs** (0.4/0.8/1.5 vs 0.5/1.0).
   That is exactly what "thousands of stop and target combinations optimised per session"
   would produce. The multiples are parameters, not constants.
3. R differs enormously (15.50 pts vs 5.85 pts) → the stop is **structural**, derived from
   price/indicator geometry, not a fixed tick offset. Confirmed independently by the panel
   (§4.3) which exposes 5 stop sources.
4. ATR is displayed on the panel (4.98) with R ≈ 5.85 → R ≈ 1.17 × ATR in that sample.
   ATR is probably a floor/sanity clamp on the structural stop, not the stop itself.

### 4.2 Order naming and scale-out

Signal names are `<PREFIX>_Entry`, `<PREFIX>_TP1`, `<PREFIX>_TP2`, `<PREFIX>_TP3`
(NT8 order signal names, visible on the chart execution labels).

Image (1) fills, one session:
```
B_Entry  7 @ 23713.50      (two entries of 7 → 14 total)
B_TP1    4 @ 23706.50
B_TP1    2 @ 23706.50
B_TP2    5 @ 23701.25
B_TP3    2 @ 23690.50
B_TP3    1 @ 23690.50      Σ exits = 14 ✓
```
A 7-lot splitting 4/2/1 = 57/29/14% is consistent with a **50/30/20 split rounded**
(3.5→4, 2.1→2, 1.4→1). Leading hypothesis, not confirmed.

Image (2): `A_Entry 10 @ 26853.75` → `A_TP1 7 @ 26855.75` (70% off at TP1), and the
later trade `A_Entry 14 @ 26877.25` → `A_TP1 8` / `A_TP2 6` (57/43, no TP3).
So the split ratio is **also configurable**, and the number of active tiers varies (2 or 3).

Entry fill vs signal price: image (2) signal box reads `BUY 26853.00`, fill `26853.75`
(+3 ticks MNQ). Image (1) signal `SELL 23713.75`, fill `23713.50` (+1 tick favourable).
Consistent with a **stop-market entry placed beyond the signal candle**, not a limit.

### 4.3 Control panel — full inventory

Read off the upscaled crop of image (2):

```
AUTO-TRADE  [ON]                              READY     ← status: READY / ...
Signal      [ON]        Break     [OFF]                 ← two independent entry engines
Buy         [ON]        Sell      [ON]                  ← direction gates
Account:    < APEX1261870000003 >                       ← account cycler (prop account)
[ Flatten ] [ BE ]                       [ Lock Out ]
[ Manual Buy ]                          [ Manual Sell ]
Risk:   [0.5x] [ 1x ] [1.5x]                            ← 1x selected (cyan)
SL Sig: [Candle] [Swing] [MA] [E50] [Man]               ← Candle selected
SL GC:  [Candle] [Swing] [MA] [E50] [Man]               ← Candle selected
ATR: 4.98
```

Image (1)/(3), light theme, adds:
```
Window: 17:00 ET (10h 17m)      ← session window + live countdown to close
```
and omits Lock Out / Manual buttons / Risk / SL selectors — the Product B build is a
reduced panel. The AutoTrader V3 panel is the superset.

**Derived requirements:**
- Two entry engines toggled independently: `Signal` and `Break`.
- Two independent stop-source selectors, one per engine, 5 options each:
  previous **Candle**, **Swing** point, **MA**, **E50** (EMA-50), **Man**(ual).
- Discrete risk multiplier (0.5× / 1× / 1.5×) applied on top of a base size.
- Direction gates (long-only / short-only / both) as one-click toggles.
- Account selector *inside the panel* → the strategy is account-aware, not bound to the
  chart's account. This is what enables the "cascade to 10 funded accounts" claim.
- `Flatten` (close all now), `BE` (pull stops to breakeven now), `Lock Out` (kill switch,
  no new entries).
- `Manual Buy` / `Manual Sell` route a discretionary entry through the **same** bracket
  machinery — this is a real design constraint, not a nicety.
- A status word (`READY`) → there is an explicit state machine driving the panel.

### 4.4 Chart visuals — inventory

| Element | Observed | Read |
|---|---|---|
| Trend ribbon | Filled band between two MAs, green bullish / dark-red bearish | Fast/slow MA cloud, regime colouring |
| Slow line | Single MA well below/above the ribbon | The `E50` stop source and/or trend filter |
| HTF box | Cyan rectangle labelled `4H`, edges `PH High` / `PH Low`, projected forward | **Previous-period high/low box** — this is the "Box" |
| Signal marker | `BUY 26853.00` / `SELL 23713.75` coloured box at the signal bar | Entry trigger label |
| Level lines | `Entry` white, `Stop` red, `TP1..TP3` green dashed, all price-labelled at right | Live bracket overlay |
| Bar strip | Row of dots/blocks along the bottom, green/red | Per-bar regime or condition-met state |
| Dashboard | Daily P&L, last 5 trades w/ signed P&L + proportional bar, equity sparkline (orange below 0 / green above), W/BE/L pie, win-rate %, profit | Session performance HUD |

The 4H `PH High` / `PH Low` box is the single most useful visual: it is the higher-timeframe
range the `Break` engine breaks out of. **That is where the project name lands.**

### 4.5 Session behaviour

- Countdown `Window: 17:00 ET (10h 17m)` → hard session window with auto-flatten at close.
- Vendor claims separate optimisation for **Asia / London / New York** windows.
- Panel is per-chart, state persists across the session (trade counter reaches #8).

---

## 5. What we cannot recover, and what to do about it

| Unknown | Why | Our move |
|---|---|---|
| Exact "Golden Candle" definition | Never published | Build our own candidate signals; let the optimiser choose |
| Exact zone construction | Never published | Use published-standard HTF range / IB / prior-session levels |
| "Trend Theory" / "Volume Bubbles" | Marketing names for unpublished internals | Ignore. Do not chase vendor vocabulary |
| Ribbon MA types & periods | Not legible at screenshot resolution | Parameterise; optimise |
| Exact scale-out ratios | Varies between samples | Parameterise; optimise |
| Their actual live performance | Only cherry-picked demo sessions exist | Assume nothing. Our own gates apply |

**Do not treat their claimed PF 1.69 / 72.9% WR as a target to hit.** It is an unverified
vendor backtest on their own data with their own assumptions. Our bar is the workspace's
own approved table in memory `[[strategy-profitability-gates]]`, out-of-sample only.

---

## 6. NT8 feasibility map

Every observed feature maps to a documented NinjaScript API. Nothing here needs an
external bridge — matching their "no external bridge" claim.

| Feature | NT8 mechanism | Risk |
|---|---|---|
| On-chart interactive panel | **`UserControlCollection`** + WPF controls, updated via `ChartControl.Dispatcher.InvokeAsync()` | Low — documented. Use `UserControlCollection`, **not** `ChartControl.Children.Add()`, which loses the controls when a strategy is added/removed |
| Level lines + labels | `Draw.Line` / `Draw.Text` or `OnRender` | Low |
| Ribbon / cloud | Plot + `Brushes` region fill, or hosted indicator via `AddChartIndicator()` | Low |
| HTF 4H box | `AddDataSeries(BarsPeriodType.Minute, 240)` + `Draw.Rectangle` | Low |
| 3-tier partial exits | `ExitLongLimit(qty, price, "TP1", "Entry")` per tier, distinct signal names | Medium — needs correct OCO/qty bookkeeping |
| Structural stops (5 sources) | Own calc from `Low[]`/`High[]`, swing detection, MA/EMA(50), manual field | Low |
| Breakeven on TP hit | `OnExecutionUpdate` → amend stop via `ChangeOrder()`/`SetStopLoss` | Medium — order-event race, see memory `[[nt8-order-event-race]]` |
| Trailing stop | `SetTrailStop()` or manual amend | Low |
| Flatten / Lock Out | Panel → flag → `CloseStrategy()` / gate entries | Low |
| Account selector + cascade | `Account` / `Instrument` lookup, submit per account (likely needs **Unmanaged** or an AddOn-style multi-account submitter) | **High** — the hardest piece. Managed approach is bound to the strategy's account |
| Session window + countdown | `ToTime()` gate + timer on the panel; `IsExitOnSessionCloseStrategy` | Low |
| Session HUD (P&L, W/L, sparkline) | `SystemPerformance.RealTimeTrades` / `GetTrades()` + custom render | Low |
| Walk-forward self-optimiser | **Not a strategy feature.** Needs an AddOn or an external harness driving the Strategy Analyzer, or an in-strategy replay of candidate configs | **High** — this is the real engineering |

Two genuinely hard parts: **multi-account cascade** and **the self-optimiser**. Everything
else is routine NinjaScript.

---

## 7. Proposed shape for BreakBox

The honest conclusion from §5 is that copying their signal is impossible *and unnecessary*.
Their moat is not the signal — it is the **shell + the validator**. So we build those, and
we put our own signal in the socket.

**BreakBox = a box-breakout engine inside a session-aware execution shell.**

The name is not arbitrary: their `Break` toggle, the 4H `PH High`/`PH Low` box, and the
bundled Initial Balance Bot (IB range → extension → retrace) are all the same idea —
*define a box, trade its break or its retrace*. That is a published, well-understood family
of setups we can build honestly and test properly, unlike "Golden Candles".

Suggested layering, each independently useful:

1. **Layer 0 — Execution shell.** Bracket engine: structural stop (5 sources) + N-tier
   R-multiple targets + BE-on-TP + trailing. Signal-agnostic. This is the piece that pays
   off across every other strategy in `projects/Trading/`.
2. **Layer 1 — Control panel.** `UserControlCollection` WPF panel: engine toggles,
   direction gates, risk multiplier, stop-source selectors, Flatten/BE/Lock Out, manual
   entries, window countdown, status.
3. **Layer 2 — Session HUD.** Daily P&L, trade list, W/L, equity sparkline.
4. **Layer 3 — Box engine.** HTF/session/IB box construction + break and retrace entries.
5. **Layer 4 — Validator.** Walk-forward: optimise on 70%, prove on the held-out 30%,
   per session window, rank surviving configs, refuse to arm an overfit one.
6. **Layer 5 — Multi-account cascade.** Only if layers 0–4 clear the profitability gates.

Layers 0–2 are pure engineering with no edge risk — they are worth building regardless of
whether the box signal survives. Layer 4 is where the actual intellectual value sits.

---

## 8. Open questions for Javier

1. **Scope.** Full shell (layers 0–5, a real product-grade rebuild) or just the box signal
   to test whether the edge exists at all (layer 3 + minimal bracket)? Recommendation:
   **layer 3 + layer 0 first** — prove the edge before building the cockpit around it.
2. **Instrument/timeframe.** Their demo is MNQ. Our NQ tooling and data (PropSim,
   `[[nq-continuous-dataset]]`) are the deepest. Default to NQ/MNQ unless told otherwise.
3. **Did you buy it?** If there is legitimate access to the running product, we can
   observe far more behaviour (settings dialog, Strategy Explorer, actual parameter names)
   without touching the binary. That would sharpen the spec enormously. If not, this
   document is the ceiling of what observation gives us.
4. **Reuse.** `PatternZone`, `VeeSnap` and `LatigoBreak` already contain bracket, ATM and
   PropSim-mirror machinery. Layer 0 should be lifted from the best of those rather than
   written fresh.

---

## Sources

- [Product A (the autotrader) — official site](the vendor's site)
- [Product A (the autotrader) — NinjaTrader (the storefront)](the vendor storefront
- [Product B (the signal build) — Golden Candle Signals / Product B](the vendor storefront
- [NinjaTrader — UserControlCollection](https://ninjatrader.com/support/helpguides/nt8/usercontrolcollection.htm)
- [NT8 forum — adding controls inside the chart control](https://forum.ninjatrader.com/forum/ninjatrader-8/add-on-development/97609-adding-controls-inside-the-chart-control)
