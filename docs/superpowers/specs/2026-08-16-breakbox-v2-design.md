# BreakBox v2 — design

**Date:** 2026-08-16
**Status:** approved in chat · revised after a 4-lens adversarial review (20 blockers fixed)
**Supersedes:** `docs/design.md` (v1), which described a build that took **zero trades**
**Provenance:** `docs/research/01-target-analysis.md` + a 14-agent forensic pass over the reference
screenshots + a 439-session measurement on `projects/Trading/NQData`

> **Every code reference in §11 was verified against the files on disk on 2026-08-16.** Two
> descriptions in the first draft of this spec were wrong and are corrected there. Line numbers
> drift — re-verify before acting on any of them.

---

## 0. Clean room

Unchanged and non-negotiable. We rebuild **observed behaviour** measured from screenshots. No
decompiling, no circumventing their licensing, no reuse of their names, order prefixes (`A_`,
`B_`) or branding, and **this repo stays private**. v2 order signal names are ours:
`BB_CloudLong` / `BB_CloudShort` / `BB_BoxLong` / `BB_BoxShort`.

---

## 1. Why v1 took zero trades

A dimensional error, not a bug.

```
Box #17:  29867.25 / 29592.25   →  275.00 points   (a 240-MINUTE range)
ATR:      7.85                                      (a 14-BAR average on 30s bars)
275 / 7.85 = 35 ATR             MaxBoxRangeAtr = 6  →  invalid  →  no engine ever runs
```

**The box is a wall-clock object; the ATR is a bar-count object; the validity band divides one by
the other.** No tuning of the band fixes it — it is measured in the wrong units.

Two compounding errors sat on top:

1. **The wrong engine was primary.** The reference panel reads `Signal ON / Break OFF`. Every trade
   in that session came from *Signal*. v1 built `Break` as primary.
2. **The wrong box.** The cyan `4H / PH Low` rectangle is distant context. The object actually
   traded is a **micro accumulation** drawn as a small white rectangle around ~7 bars.

---

## 2. What the evidence says

**M** = measured off pixels · **I** = inferred from a measurement · **S** = single sample, treat as
a hypothesis. The first draft of this spec labelled the whole table "measured"; that was wrong.

| Element | Value | Tag | How |
|---|---|---|---|
| Ribbon fast | EMA(10) | **M** | alpha-solve 8.8; forward-sim best fit 10 (RMSE 11px); SMA fits 2–70× worse |
| Ribbon slow | EMA(23) | **M** | alpha-solve 24; forward-sim 23 (RMSE 2.0px) |
| Slow line | EMA(52) | **M** | alpha-solve 51.6; forward-sim 52 (RMSE 1.5px); panel has a literal `E50` button |
| Bar size | 30 seconds | **I** | see §2.2 |
| Entry order type | stop, not limit | **M** | both fills WORSE than signal (3 and 1 ticks) |
| Entry price | at the signal bar's high | **I** | signal 26853.00 vs bar high 26852.99 — see §2.3 |
| Stop source | `Candle`, R ≈ 1 ATR | **S** | R/ATR = 0.82 and 1.17 — **not disambiguated, see §16** |
| Targets | TP1 = 0.50R, TP2 = 1.00R | **M** | held across both trades while R changed 43% |
| Target anchor | the **signal** price, not the fill | **M** | see §2.4 — this is a design decision, not a detail |
| Scale-out | 7/6 (54/46) and 8/6 (57/43) | **M** | read off the exit labels |
| BE-on-TP1 | on | **M** | `A_Stop` exited at 26853.75, identical to the entry fill |
| Signal candle | painted yellow, both wickless | **M** | exactly 2 yellow bodies in the frame, both at entries |
| Accumulation | one white rectangle, ~7 bars | **M** | x 921.5–1030, y 206–268; the entry bar's body sits entirely above its top edge |
| Hold time | 30–60 seconds (1–2 bars) | **M** | entry and exit markers share a bar column |
| Cadence | ~1 trade / 34 bars (~17 min) | **S** | one measurable gap only |
| Capture | 1.84 pts/contract/trade blended | **M** | 12.9 pts across 7 trades |
| Size | fixed 13–14 MNQ, not risk-normalised | **I** | risk varied 54% between trades at `Risk 1x` |

Both entries share one shape: **price pulls back INTO/THROUGH the ribbon, closes back out of it in
the regime direction, and the entry goes on the continuation bar** — never on the touch.

### 2.1 Corrections to `docs/research/01-target-analysis.md`

- Trade #6 was **13 contracts, not 10**; TP1 **26855.00, not 26855.75** (only qty 7 at 1.25 pts of
  *captured* distance yields the HUD's exact $17.50).
- The HUD's P&L is **gross**. Trade #7 reconciles to the cent with no commission deducted.
- The only loss (−$50.50) was a **multi-leg partial, not a −1R stop-out**. That demo never showed
  its tail.

### 2.2 The bar size, with the arithmetic shown

The first draft scaled from the wrong base. Correct: the anchor is **the user's own chart**
(30s ATR(14) = 7.85), scaled by √time to ask what the same tape would read at other bar sizes.

```
30s → 7.85 (anchor)     1m → 7.85×√2  = 11.1     2m → 15.7     5m → 7.85×√10 = 24.8
```

The reference reads **4.98**, which is *below* the 30s figure and far below every larger one. So
the reference cannot be on 1m or 5m; it is 30s (or faster) on a quieter tape. Absolute sanity
check via implied daily range: 5m → ~0.20% of price (NQ's calmest days are ~0.6% — impossible),
1m → ~0.44% (essentially impossible), 30s → ~0.62% (a quiet but real day).

**Reconciling 4.98 against the user's 7.85:** ratio 0.634, i.e. the reference session ran at ~40%
of the user's variance. Their session countdown (17:00 ET with 10h17m left → ~06:43 ET) puts them
**pre-RTH**, where NQ 30s ATR routinely runs half the 09:30–10:30 value. Same bar size, quieter
regime. `NQData`'s median **RTH** 30s ATR is 9.61 — higher than the user's 7.85 because his
screenshot spans 12:01–14:45, the midday lull. All three numbers are consistent; none of them
"corroborates" another and this spec no longer claims they do.

### 2.3 Trigger offset: the evidence is ambiguous

Signal 26853.00 vs a measured bar high of 26852.99. On the 0.25 grid that high **is** 26853.00, so
the observation supports a trigger **at** the high just as well as at high + 1 tick. Measurement
noise is ±0.4 tick. `TriggerOffsetTicks` is therefore a **G** parameter, default 1, search 0–2 —
not a measured constant.

### 2.4 Target anchor — a real fork

The reference computes its targets from the **signal** price and eats the entry slippage out of the
target: trade #6's TP1 was 2.05 pts from signal (0.50R at R = 4.10) but only **1.25 pts from the
fill**, because the stop entry filled 3 ticks worse. v1 anchors on the **fill**, which makes R the
honest risk actually taken.

**v2 keeps the fill anchor and exposes `TargetAnchor = Fill | Signal` (default `Fill`).** Rationale:
anchoring on signal means every tick of adverse entry slippage is deducted from an already-tiny
0.5R target — on the user's tape that is 3 ticks off a 3.1–4.7 pt TP1, a 16–24% haircut. The dial
exists because reproducing their behaviour exactly is one click, and because the difference is
measurable in the §13 protocol.

---

## 3. The measured null result, stated honestly

A skeptic loaded `NQ_continuous_1s.npz` (439 sessions, 2024-11-22 → 2026-08-04, 321,580 RTH 30s
bars) and measured the **box compression-break**:

```
directional forward return, n = 4,094
    4 bars: +0.001 ATR (t +0.05)     10 bars: +0.007 ATR (t +0.21)
   20 bars: +0.038 ATR (t +0.81)     40 bars: +0.050 ATR (t +0.76)
proportion following through at 20 bars: 50.1%
```

*(The first draft printed the t-statistic and the proportion on one line, which reads as if
t = +0.81 tested the 50.1%. It does not: t = +0.81 tests the mean return; the proportion has its
own, even smaller, statistic. Two measurements, both null.)*

~30 variants — stop / retest / open entries, with and without BE, several target sets, longs only,
shorts only, fade — all came back **net negative**. A random-entry control through the identical
bracket was also negative, and the signal-versus-random gap sits inside noise.

**Two things this kills and one it does not.**

- It kills **the box-break as a directional signal at the tested scale and validation**.
- It raises a **question** about the bracket: `0.5/1.0/1.5R + BE-on-TP1` was gross-negative even on
  random entries (−0.469 pts), and removing BE improved every variant. See §7 for why that is not
  yet sufficient grounds to change the default.
- It does **not** test the **cloud-reclaim** hypothesis — the one that matches both observed
  entries. That remains unmeasured.

**Decision on record:** the user was shown this result and chose to build the box engine anyway,
intending to optimise it himself. That is his call and v2 builds it, fully specified in §6, to the
same standard as the cloud engine. There is also a technical argument: what was measured is the box
*as v1 defined it* — a 240-minute range validated against a bar-count ATR. §6 defines a different
object with a different lifecycle.

### 3.1 A correction to the analysis itself

One critic reported the target failing the house gates "by 11–13×". **That is wrong.** It compared
MNQ dollars against the dollar column of `[[strategy-profitability-gates]]`, calibrated for NQ (10×
the point value). In R, which is instrument-independent:

```
blended capture 1.84 pts/contract/trade ÷ mean R 4.975 pts   =  0.37R gross
costs 20–36% of gross                                        →  0.24–0.29R net
house gate: ≥0.10R minimum, 0.20–0.35R ideal
```

It lands **inside the ideal band**. Unit-independent concerns that do stand: **commissions/gross at
20–36%** against a ≤30% ceiling, and **n = 7** against a required ≥100. The correct verdict on the
vendor's demo is **no evidence**, not *fails*.

---

## 4. Architecture

| File | Responsibility | Pure? |
|---|---|---|
| `BreakBoxTypes.cs` | bars, swings, Wilder ATR, EMA, rounding, window math | yes |
| `BreakBoxCloud.cs` | **NEW.** Cloud engine + `BbCloudState` | yes |
| `BreakBoxCore.cs` | **REWRITTEN.** Box engine + `BbEngineState` | yes |
| `BreakBoxExits.cs` | bracket — unchanged | yes |
| `BreakBoxHistory.cs` | **NEW.** `BbTradeRecord` + serialise/parse + equity reduction | yes |
| `BreakBoxStrategy.cs` | series, orders, session, governor, scaling, **history file I/O** | no |
| `BreakBoxPanel.cs` | **REWRITTEN.** Vertical sidebar + HUD + history chart | no |
| `BreakBoxVision.cs` | **NEW.** Indicator: cloud, boxes, gold candles, markers | no |

"Pure" means zero `using NinjaTrader.*`, so `tests/BreakBox.Tests.csproj` compiles it. **File I/O is
not pure** — `BreakBoxHistory.cs` therefore contains only the record type, a hand-rolled
serialiser/parser (no `System.Text.Json`: NT8 is .NET Framework 4.8 / C# 7.3, the runner is net8,
and no serializer sits on both reference paths) and the pure equity reduction. The actual
`File.AppendAllText` / `File.ReadAllLines` against `NinjaTrader.Core.Globals.UserDataDir` lives in
`BreakBoxStrategy.cs`.

Both engines return the same `BbAction` and feed the same `BbExits` bracket.

### 4.1 Engine arbitration — what happens when both are ON

Undefined behaviour here would be invented by whoever implements it, so it is specified.

- **One live trigger and one position at a time**, across both engines.
- Engines are evaluated in fixed order — **Cloud, then Box**. The first to return `Fire = true`
  wins; the other is not evaluated on that bar.
- `BbAction.Engine` gains a `Cloud` member (`BbEntryEngine { Break = 0, Retrace = 1, Cloud = 2 }`).
  `Retrace` is retired but its enum value is retained so saved workspaces do not shift.
  `BoxHigh` / `BoxLow` / `BoxId` are **0** for cloud actions and the panel must not read them when
  `Engine == Cloud`.
- The shell records `_owningEngine` on submit and forwards expiry, disarm and rejection **only** to
  that engine. This is what makes §11 B7's "single owner" implementable.
- A suppressed action (another engine armed, position open, entry pending) is **not consumed**: the
  token stays minted and the attempt counter is not decremented. It is logged to §9.4 as
  `suppressed: cloud (box armed)`.
- The `engine` field in §10's history record stores the winner.

### 4.2 Gate reporting

Each engine owns its own `BbGateReport { string Block; string BlockDetail; int GateDepth; }` — one
inside `BbCloudState`, one inside `BbEngineState`. They never share an instance. The panel renders
the report of the engine that would act next under §4.1 ordering (Cloud when `EnableCloud`, else
Box), so two engines can never overwrite each other's ladder.

---

## 5. Engine A — Cloud

The reference's `Signal` engine, and the primary.

### 5.1 State

All cloud memory lives in **`BbCloudState`** (`BreakBoxCloud.cs`): `regimeLatched`,
`regimeLatchedAgeBars`, `armed`, `ext`, `ageBars`, `barsSinceLastArm`, `triggerArmedBars`,
`gate` (a `BbGateReport`), and a circular buffer of `eT` values sized
`cfg.TrendSlopeLookback + 1`, allocated at `State.DataLoaded`. **No hard-coded buffer length** — a
fixed 12 slots silently under-reads the moment the converted lookback exceeds it.

Indicators come from the shell: `eF`, `eS`, `eT`, `atr`. Setting `MaPeriod = RibbonSlow` makes
`BbStopSource.Ma` mean *"stop at the far ribbon edge"* — the only candidate matching the ambiguous
stop measurement (drawn 26871.17 vs extrapolated EMA23 26870.98, 0.19 pts apart). §16.

### 5.2 Per closed bar, in this order

**Step 0 — update first.** Update `atr`, `eF`, `eS`, `eT` with **this** closed bar and push
`eT.Value` onto the slope buffer, *before* any gate reads them. `eT[k]` therefore means the value k
bars before this one. Every comparison in steps 2–5 is against post-update values. *(This matches
the existing shell, which updates at `Strategy.cs:334-336` before calling the engine. It is stated
because `close > eF` means something materially different under the other convention — the EMA has
already absorbed the close it is being compared against.)*

**Step 1 — warmup.** If `!atr.IsWarm || !eT.IsWarm || !eS.IsWarm`, set the gate report and return.
Steps 2–5 are all suppressed: the values they would read are not yet meaningful.

**Step 1b — the `canTrade` boundary.** `canTrade == false` (auto-trade off, lockout) suppresses
**only step 5 and the arming of a trigger**. Steps 2–4 — regime, token mint, token kill — run
unconditionally on every closed bar. Otherwise ten minutes with AUTO-TRADE off leaves a hole in the
state and re-enabling resumes from a stale regime. `canTrade` is **passed into** `OnBar`; today it
is computed at `Strategy.cs:385` and merely discarded at `:391` after the engine has already
mutated (§11 B2).

**Step 2 — regime, latched.**
```
slopeRaw = eT.Value − eT[TrendSlopeLookback]
slope    = slopeRaw / TrendSlopeLookback          ← per-bar, so the gate survives a bar-size change
regimeNow = +1 iff close > eT && eF > eS && eS > eT && slope >= +TrendSlopeAtr*atr/RefLookbackBars
regimeNow = −1 iff close < eT && eF < eS && eS < eT && slope <= −TrendSlopeAtr*atr/RefLookbackBars
else 0
if (regimeNow != 0) { regimeLatched = regimeNow; regimeLatchedAgeBars = 0; }
else regimeLatchedAgeBars++
```
**The latch is the fix for the single worst defect in the first draft.** The token is minted by a
pullback that touches the far edge `eS` — and a pullback deep enough to touch `eS` normally drags
`eF` to or below `eS` within a bar or two, which would zero the instantaneous regime and kill the
token *before* the reclaim bar it is waiting for. The engine would mint and destroy on the same
move, every time: v1's self-cancelling latch in a new costume. Steps 3–5 therefore test
`regimeLatched`, never `regimeNow`.

`regimeLatched` clears only on: the **opposite** regime forming · a close through `eT` against it ·
`regimeLatchedAgeBars > RegimeMemory`.

**Step 3 — mint the token** (long shown; the short is the exact mirror with High↔Low, `>`↔`<`,
`min`↔`max`):
```
if (!armed && regimeLatched == +1 && bar.Low <= eS.Value) {
    armed = true;  ext = bar.Low;  ageBars = 0;          // ext ASSIGNED, not min()-ed
}
else if (armed) {
    ageBars++;  ext = Math.Min(ext, bar.Low);            // a re-touch deepens ext, does NOT reset ageBars
}
```
The `else if` is load-bearing three ways: the touch bar itself has `ageBars == 0` so the trigger
cannot fire on it (matching §2's "never on the touch"); `ext` is assigned on mint rather than
folded into a stale value from a previous token; and price riding the ribbon cannot reset the clock
indefinitely and defeat `PullbackMax`.

**The token is a consumable minted only by a physical touch of the FAR cloud edge.** That is the
answer to *"why doesn't it fire every bar in a trend"*: a runaway trend that never touches `eS`
again produces exactly **one** trade. Unlike v1's box latch — whose clearing condition was
near-impossible, which is why the engine was mute — this clearing condition is ordinary and
recurrent. It throttles without muting.

**Step 4 — kill the token** if any of: `close` closes through `eT` against `regimeLatched` (the
reference's deepest pullback, 26864.83, still sat 4.3 pts above `eT` and never closed through it) ·
`ageBars > PullbackMax` · `regimeLatched` flipped sign. On kill set `ext = double.NaN`.
*"Regime went to 0"* is **not** a kill condition — see step 2.

**Step 5 — the trigger (gold candle).** Fires only if `armed && !double.IsNaN(ext) &&
ageBars >= MinPullback && barsSinceLastArm >= MinBarsBetween`, and ALL of:

| # | Long | Short | Default | Tag |
|---|---|---|---|---|
| a | `close > eF` | `close < eF` | — | reclaim |
| b | `close > open` | `close < open` | — | direction |
| c | `(close−low)/(high−low) >= CloseInRange` | `(high−close)/(high−low) >= CloseInRange` | 0.60 | **C** — both reference candles were wickless (ratio 1.00) |
| d | `(high−low) >= MinBarRangeAtr*atr` | same | 0.20 | **C** — the right-hand reclaim bar was 0.30 ATR, so anything >0.30 rejects a trade we *know* was taken |
| e | `(high−ext) >= MinLegAtr*atr` | `(ext−low) >= MinLegAtr*atr` | 0.35 | **G** |

**Step 6 — on trigger.** `TriggerPx = RoundToTick(bar.High + TriggerOffsetTicks*tick)` for a long
(`bar.Low − …` for a short). `SignalBarHigh/Low = bar.High/bar.Low` feed the existing `Candle` stop.
Consume the token (`armed = false; barsSinceLastArm = 0`) **only if the shell accepts the action**
— see step 9.

**Step 7 — trigger life.** `triggerArmedBars++` while a trigger is working. On
`triggerArmedBars > TriggerLife`, cancel and **restore the token** (`armed = true`, `ext` and
`ageBars` preserved) if `regimeLatched` still holds. The first draft burned the token on expiry,
which is precisely the defect §11 B3 condemns in the box engine — but the fix is NOT symmetric
between the two engines. The cloud's token is a re-usable consumable (mint → spend → re-mint on the
next touch), so restoring it on expiry costs nothing extra. The box's arm is a bounded per-edge
BUDGET, and there the two disarm paths deliberately diverge (§6.2): expiry still SPENDS an arm — the
market declined a live order — while only a refusal (cancelled/rejected) REFUNDS one, because that
one we declined ourselves.

**Step 8 — re-arm** otherwise requires a new touch of `eS` while `regimeLatched` holds.

**Step 9 — rejection.** `OnEntryRejected(engine, reason)` restores the token exactly as step 7
does. Every refusal path in the shell calls it (§11 B4).

### 5.3 Paint — the free diagnostic

- all gates pass → `Gold`
- **bar** gates (b,c,d) pass but **context** gates (a-reclaim, token, e-leg) fail → `DarkGoldenrod`

Grouping fixed: condition (a) is a *context* gate, not a bar gate — the first draft grouped the
five three different ways in three sections and let (a) fall through all of them.

**Single owner of `BarBrushes`:** the painting lives in **`BreakBoxVision.cs` only**. The strategy
never writes bar brushes. Two components painting the same pixels on the same chart is a bug that
looks like a rendering glitch. §12.

### 5.4 Parameters

Every one tagged **M** (measured), **C** (calibrated against a known-taken trade) or **G** (guess).
**No unmarked constants. No horizon in bars.**

| Parameter | Default | Tag | Search |
|---|---|---|---|
| `RibbonFastSec` | 300 (=EMA10 @30s) | M | 240–360 |
| `RibbonSlowSec` | 690 (=EMA23 @30s) | M | 540–780 |
| `TrendLineSec` | 1560 (=EMA52 @30s) | M | 1440–1680 |
| `TrendSlopeSec` | 300 (=10 bars @30s) | G | 150–600 |
| `TrendSlopeAtr` | 0.15 | G | 0.05–0.40 |
| `RegimeMemorySec` | 900 | G | 300–1800 |
| `PullbackMaxSec` | 600 | G | 300–1200 |
| `MinPullbackSec` | 30 | G | 30–120 |
| `CloseInRange` | 0.60 | C | 0.50–0.80 |
| `MinBarRangeAtr` | 0.20 | C | 0.0–0.30 **only** |
| `MinLegAtr` | 0.35 | G | 0.20–0.80 |
| `MinBarsBetweenSec` | 180 | G | 60–600 |
| `TriggerLifeSec` | 120 | G | 30–240 |
| `TriggerOffsetTicks` | 1 | G | 0–2 |

Sanity check on `TrendSlopeAtr`, against the converted lookback: `eT` moved 26842.9 → 26861.5 over
34 bars = 0.55 pt/bar. Over a 10-bar lookback that is 5.5 pts = **1.10 ATR**, so the reference
clears the 0.15 default by 7×. Soft where the reference lived, hard in chop — the intent.

---

## 6. Engine B — Box

Built at the user's explicit direction. Fully specified to the same standard as §5; the first draft
left it as prose and it was not implementable.

### 6.1 Box lifecycle — an explicit state machine

The first draft mixed a rolling window with talk of "sealed" boxes, which have no seal event. A box
now has identity and a lifecycle.

**FORMATION.** Over the last `BoxLookback` **closed** bars (current bar excluded — including it is
a one-bar lookahead), compute `winRange = MAX(High,N)[1] − MIN(Low,N)[1]`. A candidate exists when
`winRange <= percentile(trailing winRange samples, BoxRangePctile)`. One `winRange` sample is
appended to a ring of `BoxSampleN` **every bar**, independent of formation, so the distribution is
not self-selected — gating the sample on the test would be a feedback loop that tightens forever.

**SEAL.** The candidate freezes its High/Low and takes a monotone `Id` on the first bar the
formation test still holds after `BoxMinBars`. Nothing moves a sealed box's edges.

**INVALIDATE.** The box dies on a close beyond either edge by more than `BoxDeadAtr × atr`, or at
`age > BoxMaxAge`. A dead box is replaced by the next candidate to seal.

**VALIDITY GATE.** `winRange / mean(last BoxMeanSamples ranges of boxes sealed strictly before this
one) ∈ [BoxValidLo, BoxValidHi]`. Dimensionless and timeframe-independent by construction — this is
what `MinBoxRangeAtr` / `MaxBoxRangeAtr` were trying to express, and its absence is the v1 defect.

**COLD START.** Until `BoxMeanSamples` boxes have sealed, the box engine is **hard-disabled** and
says so in the gate ladder. There is no cross-session seed — `BbTradeRecord` carries no box-range
field, and nothing in the shell reads history back into the sealed-range ring — so every fresh
attach warms up from scratch. At the defaults (`BoxLookback` 7 bars, `BoxMinBars` 2, `BoxMeanSamples`
20) the first box can seal as early as bar `BoxLookback + BoxMinBars` = 9 (~4.5 minutes on a 30s
chart) in the best case, but there is no fixed ceiling after that: a live box blocks the next
candidate from sealing until it dies — a break or up to `BoxMaxAge` bars — so how long 20 boxes
takes depends entirely on the tape and has never been measured against real data. The panel's own
`BOX WARMING — n/N boxes sealed` readout is the actual clock; do not assume day 1 finishes warming.
An undefined cold start is the other way to reproduce v1's silence.

### 6.2 Entry

Break of a sealed, valid box edge, same stop-market mechanism as §5.2 step 6. **Arming does not
spend the edge as a boolean latch:** `BoxArmsPerEdge` arms per edge per box Id, each separated by
`BoxArmCooldown`, the counter reset on an inside close (a close within both edges of the **sealed**
box). v1's single latch meant an expired, cancelled or refused trigger burned the box without
trading. v2 replaces it with a bounded budget, and the two disarm paths deliberately do NOT collapse
to the same rule: `OnEntryRejected` (cancelled/rejected — we refused the trade ourselves) refunds the
spent arm, but `OnTriggerExpired` (the market declined a live order) does not — the arm stays spent.
Pinned by `ArmingDoesNotSpendTheEdge` and `ExpirySpendsAnArmRejectionRefundsIt` in `BoxTests.cs`.

Drawn as a **white rectangle**, matching the reference.

### 6.3 Parameters

| Parameter | Default | Tag | Search |
|---|---|---|---|
| `BoxLookbackSec` | 210 (=7 bars @30s) | C — matches the measured ~7-bar white rectangle | 90–600 |
| `BoxMinBars` | 2 | G | 1–5 |
| `BoxRangePctile` | 35 | G | 20–60 |
| `BoxSampleN` | 200 | G | 100–400 |
| `BoxMeanSamples` | 20 | G | 10–50 |
| `BoxValidLo` / `BoxValidHi` | 0.4 / 2.5 | G | 0.2–0.6 / 1.8–4.0 |
| `BoxDeadAtr` | 0.5 | G | 0.25–1.5 |
| `BoxMaxAgeSec` | 1800 | G | 600–3600 |
| `BoxArmsPerEdge` | 2 | G | 1–4 |
| `BoxArmCooldownSec` | 180 | G | 60–600 |

### 6.4 Consequences elsewhere

The 240-minute slot machinery is **deleted**, and with it `SlotOf`, `BbBoxSource`, `HtfMinutes`,
`IbStartHhmm`, `IbMinutes` and `SessionCloseHhmm`. §11 B11 (the `SlotOf` off-by-one) and B12
(`BreakSpentDir` as a single int) are therefore **closed by deletion**, not by patching.

---

## 7. The bracket

`BbExits` needs **no code changes**. Recommended config, reproducing the reference geometry:
`StopSource = Candle`, `StopBufferTicks = 2`, `StopMinAtr = 0.5`, `StopMaxAtr = 3.0`,
`TierCount = 2`, `Tp1R = 0.50`, `Tp2R = 1.00`, `Tp1Pct = 50`. The Product B variant is
`TierCount = 3` at `0.40/0.80/1.50`, where the chandelier trail actually matters.

### 7.1 `BreakevenOnTp1` stays ON — reversed after review

The first draft flipped the default to OFF on the strength of ~30 measured variants where removing
BE improved every one. **That reasoning does not hold and the default is restored to ON**, for
three reasons the review surfaced:

1. Every one of those variants was measured on the **box-break signal that §3 establishes has no
   directional edge**. On a zero-edge signal the exit scheme is the only thing generating the
   number, so "BE-off is less negative" says nothing about BE on a signal that *has* an edge.
2. BE-on-TP1 is **confirmed reference behaviour** (§2), and the user's stated goal is resemblance.
3. Turning BE off creates an exposure the first draft never mentioned: with TP1 at 0.50R taking 50%
   off and no BE, the runner can round-trip from +1R territory to a full −1R. Post-TP1 open loss is
   unbounded until the stop, and `DailyLossLimit` (§11 B10) becomes the only thing catching it.

BE-off becomes a **pre-registered A/B in §13**, measured on whichever signal survives — not a
default changed on borrowed evidence.

### 7.2 Breakeven win rate — the number that decides everything

With `TierCount = 2`, `Tp1R = 0.50`, `Tp2R = 1.00`, `Tp1Pct = 50`, BE on: max gross win is
0.75R against a −1.0R loss → **gross breakeven 57.1%**. BE converts many partial winners into ~0.25R
scratches, pulling the average win toward ~0.5R → **~66.7%**. Costs of ~0.17R per round turn on the
user's tape push **net breakeven to 67–78%**, and clearing the gate's 0.10R expectancy floor needs
**75–84%**.

That is a demanding number and it is stated here so nobody discovers it after building. Two levers
if the counter run says it is out of reach: raise `Tp2R` (a 1:2 geometry needs ~40% before costs),
or move `Tp1Pct` down so more size rides to the further target.

### 7.3 Scale

On the user's tape (ATR 7.85 vs the reference's 4.98) the same ATR-relative rules give
**R = 6.3–9.4 pts, TP1 = 3.1–4.7 pts** — 1.6× the reference's absolute distances and therefore 1.6×
more headroom over the commission floor. **Carry the ATR-relative geometry, never their absolute
distances.**

---

## 8. Auto-scaling contract

The user's requirement: *"que escale sola"*.

**No horizon is expressed in bars on the parameter surface.** Every one is seconds, converted in a
single helper:

```csharp
int barSec = BarSeconds();                 // throws for non-time series — see below
cfg.RibbonFast        = Math.Max(2, RibbonFastSec / barSec);
cfg.TrendSlopeLookback= Math.Max(2, TrendSlopeSec / barSec);
cfg.TriggerLife       = Math.Max(1, TriggerLifeSec / barSec);
cfg.MinPullback       = Math.Max(1, MinPullbackSec / barSec);
cfg.PullbackMax       = Math.Max(2, PullbackMaxSec / barSec);
cfg.RegimeMemory      = Math.Max(2, RegimeMemorySec / barSec);
cfg.MinBarsBetween    = Math.Max(1, MinBarsBetweenSec / barSec);
cfg.BoxLookback       = Math.Max(2, BoxLookbackSec / barSec);
cfg.BoxMaxAge         = Math.Max(2, BoxMaxAgeSec / barSec);
cfg.BoxArmCooldown    = Math.Max(1, BoxArmCooldownSec / barSec);
```

**Where the conversion runs.** Not "once at `State.DataLoaded`" — `BuildConfigs()` is also called
by every panel toggle (`Strategy.cs:272-322`). The conversion lives **inside `BuildConfigs()`**,
with `barSec` cached at `State.DataLoaded`. That way a toggle rebuilds a correctly-scaled config
instead of a raw one.

**The slope gate must be normalised or it does not carry.** Slope over N bars scales ~linearly with
bar size while ATR scales ~√. §5.2 step 2 therefore divides the raw slope by the lookback and
compares against `TrendSlopeAtr*atr/RefLookbackBars` with `RefLookbackBars = 10`. Without this the
gate goes soft on higher timeframes and hard on lower ones — the exact defect class §8 exists to
make unreachable.

**Non-time-based series.** Javier runs tick charts elsewhere in this workspace (PatternZone is
150-tick), so a hard throw would turn "escala sola" into "no carga". Instead: for
`Tick`/`Volume`/`Range` bars, `BarSeconds()` **estimates** the median seconds-per-bar over the
loaded history and uses it, printing the estimate once and surfacing it in the panel's SESSION
block as `bar ≈ 12s (est, 150-tick)`. If fewer than 200 bars are loaded it falls back to 30 and
warns. The model is calibrated for time bars; this makes a tick chart *usable and visibly
approximate* rather than *silently wrong* or *dead*.

---

## 9. Panel v2

### 9.1 What was wrong

`BreakBoxPanel.cs` builds a `StackPanel` of horizontal `StackPanel` rows in an auto-sized `Grid`.
Width is `max(row width)`, height is `sum(row heights)` → 830×480, landscape, every row a different
length because nothing shares a column structure. The "messy rectangle" is a direct consequence of
`Row()` returning `Orientation.Horizontal` with no grid.

Worse: **it displayed `READY` while displaying `(out of band)` and never connected the two.** The
user watched a dead strategy for an hour. That is the defect this section exists to fix.

### 9.2 Layout

300 DIP, docked **left**, full height. Header and action bar are `DockPanel.Dock` Top/Bottom and
never scroll; the middle is a `ScrollViewer`. Every row sits on the same 2-column grid.

```
┌──────────────────────────────────────────────┐ 300px · LEFT · full height
│ BREAKBOX      MNQ 09-26 · 30s          [‹]  │  header — never scrolls
│ ● READY                     AUTO-TRADE [ON] │
├──────────────────────────────────────────────┤
│ WHY NO TRADE                                 │
│ armed long @ 29867.50 — 18 ticks away        │  plain-English headline
│  OK  atr warm      7.85 (14 bars)            │
│  OK  regime        long (latched 6m)         │
│  OK  token         armed 4 bars ago          │
│  ✕   gold candle   body 0.42 (need 0.60)     │  ← the blocker, amber
│  ·   window        not evaluated             │  ← short-circuited, dimmed
│  ·   budget        not evaluated             │
├──────────────────────────────────────────────┤
│ ENGINE LOG                                   │
│ 12:52  token killed — closed through E50     │
│ 12:46  suppressed: box (cloud armed)         │
│ 12:41  armed cloud_long @ 29867.50           │
├──────────────────────────────────────────────┤
│ CONTROLS                                     │
│ Engine  [ Cloud  ON ][ Box     off ]         │
│ Side    [ Buy    ON ][ Sell      ON ]        │
│ Risk    [ 0.5x ][  1x  ][ 1.5x ]             │
│ Stop    [Cndl][Swng][ MA ][E50][Man]         │
├──────────────────────────────────────────────┤
│ SESSION                                      │
│ atr 7.85   bar 30s   window 16:55  box #17   │
├──────────────────────────────────────────────┤
│ HISTORY                 [ today | 20d | 100t ]│
│                                              │
│   +$1,284.00                                 │  22px — the dominant number
│        ╱‾╲    ╱‾‾‾╲___╱‾                     │  Polyline + Polygon fill
│   ╲__╱    ╲__╱                               │
│   ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈ 0                 │  1px dashed zero baseline
│                                              │
│   47 trades   W29 BE3 L15   ·   62% win      │
│   ████████████████████░░░░░░░░                │  stacked W/BE/L, 6px
│                                              │
│   #8 SHORT 16:41  +441.00  ███████████       │  row background IS the bar
│   #7 LONG  15:02  +401.50  ██████████        │
│   #6 LONG  14:37  +186.50  ████              │
├──────────────────────────────────────────────┤
│ [  FLATTEN  ][   BE   ][  LOCK OUT  ]        │  action bar — never scrolls
│ [   MANUAL BUY       ][   MANUAL SELL   ]    │
└──────────────────────────────────────────────┘
```

### 9.3 The gate ladder

The active engine (§4.2) writes `Block` / `BlockDetail` / `GateDepth` into **its own**
`BbGateReport` at every early return. The panel renders gates passed as `OK` + value, the first
failing gate as `✕` + *what it needs versus what it has*, everything after it dimmed as
`not evaluated`.

Shell-level blocks preempt the ladder and replace the headline: `LOCKED OUT (manual)`,
`LOCKED OUT (daily loss −$450)`, `AUTO-TRADE OFF`, `ENTRY WORKING — 2 bars`, `IN TRADE`,
`BOX WARMING — 12/20 boxes sealed`.

Three live numbers that do not exist today: **distance to the nearest actionable price in ticks**
(*am I about to trade?*), **bars left on the armed trigger**, and **the token/latch state in words**
(*why has this not re-armed for an hour?*).

### 9.4 Engine log

`string[3]` ring buffer + index. Push on arm, disarm-with-reason, fill, lockout, suppression
(§4.1), and every transition into a new `Block`. Format `HH:mm  <text>`. The ladder says why *now*;
the log says what happened while you were away.

### 9.5 Stop sources

The panel exposes all five `BbStopSource` values because the enum has five. `Candle`, `Swing`,
`Manual` and `Ema50` are already implemented in `BbExits.SeedStop`; `Ma` becomes "far ribbon edge"
when `MaPeriod = RibbonSlow` (§5.1). No new code — but the panel must show which one is active in
the SESSION block, because `Ma` changes meaning with `MaPeriod` and that is invisible otherwise.

---

## 10. History persistence

The user's requirement: *a chart of previous trades and previous days, so you can tell whether the
current configuration is working.*

**Store.** One line per closed trade appended to
`<UserDataDir>/BreakBox/history-<instrument>-<account>.jsonl`, hand-serialised (§4).
Fields: `ts`, `dir`, `entry`, `exit`, `qty`, `r`, `pnl`, `engine`, `exitReason`, `cfgHash`.

**Write guard — mandatory.** The fill path and `State.DataLoaded` also execute in Strategy
Analyzer, in optimisation sweeps and on historical bars at startup. One 2,000-iteration sweep would
append tens of thousands of junk rows to the live file. Writing is gated on
`State == State.Realtime && !Bars.IsTickReplay` and skipped entirely when
`Account.Name` starts with `Backtest`. Replay writes to a `-replay` suffixed file so Replay
sessions accumulate separately and never contaminate the live curve.

**Config hash.** SHA-1 over a canonical string of every parameter that changes what is traded — the
§5.4 and §6.3 tables, the §7 bracket config, plus the panel-mutable state (`_uiStopSource`, engine
toggles, side gates). **`_uiRiskMult` is excluded**: it scales size, not the decision, and including
it would fragment the curve every time the user touches Risk. The hash is stored per trade, and
trades from a hash other than the current one render **dimmed**. A parameter change therefore shows
as a visible seam instead of silently contaminating the record — which is the whole point of the
feature.

**Read.** At `State.DataLoaded`, tail the last `N` days into memory. **Render** cumulative equity as
a WPF `Polyline` + `Polygon` fill against a dashed zero baseline. No SharpDX. **Views:**
`today | 20d | 100t`.

This is more than the reference has — theirs is session-only.

---

## 11. Shell fixes

**Every line reference below was verified against the files on disk on 2026-08-16.** Two entries in
the first draft of this spec described defects that do not exist as stated; they are corrected.

| # | Where | Defect (verified) |
|---|---|---|
| B1 | `Core.cs:210` | the box guard early-returns for **every** engine — the Cloud path must live outside `BbEngine` entirely (it does, in `BreakBoxCloud.cs`) |
| B2 | `Strategy.cs:385-391` | **corrected.** `canTrade` *is* computed before `OnBar`. The defect is that it is **not passed in**: the engine arms triggers and spends latches during warmup/lockout and the result is discarded at `:391`. Fix: pass it in, per §5.2 step 1b |
| B3 | `Core.cs:429` | arming sets the spent latch → an expired/cancelled/refused entry burns the edge without a trade. §6.2 replaces the single latch with a bounded per-edge arm budget — a cancelled/refused entry now refunds its arm, but an expiry deliberately does not (the market declined a live order; a refusal we made ourselves does not) |
| B4 | `Strategy.cs:461`, `:514`, `:733` | **corrected.** The reachable refusal paths are `qty < 1`, `CancelWorkingEntry`, and the `OrderState.Rejected` branch — *not* the degenerate-stop guard, which B14 shows is unreachable. Fix: one `OnEntryRejected(engine, reason)` called from all three, restoring the token / not decrementing the attempt counter |
| B5 | `Panel.cs:96,102,115,121,155` | toggles call `BuildConfigs()` on the **WPF thread**, swapping the engine out from under `OnBarUpdate`. Route through `TriggerCustomEvent` |
| B6 | `Core.cs:235` + `Strategy.cs:509` | **corrected.** Both clocks read the same `TriggerLifeBars = 5`; there is no 5-vs-3 disagreement. The real defect is that the engine counts `BreakArmedBars` from **arm** and the shell counts `_entryBarsWaiting` from **submit**, and neither cancels the other's object. Fix: one clock, owned by the engine (§5.2 step 7), shell mirrors |
| B7 | `Core.cs:237` | on an inside-close disarm the shell never cancels the resting stop entry. **Single owner: engine decides, shell mirrors** — implementable only with `_owningEngine` from §4.1 |
| B8 | `Strategy.cs:219-220` | the entry window is a 22-hour no-op. Default to `09:30–15:45 ET` |
| B9 | `Strategy.cs:222-223` | `MaxTradesPerBox=1` / `MaxTradesPerDay=5` are swing budgets. Raise the daily cap to 20–40 and let `DailyLossLimit` be the real governor — *how much*, not *how many* |
| B10 | `Strategy.cs` | `DailyLossLimit = 0` (off). Turn it on. §7.1 makes it load-bearing |
| B11 | `Core.cs:304` | `SlotOf` off-by-one. **Closed by deletion** (§6.4) |
| B12 | `Core.cs:166` | `BreakSpentDir` single int used as a per-direction latch. **Closed by deletion** (§6.4) |
| B13 | `Strategy.cs:385` | the EMA(50) warmup gates all trading even when the selected stop source never reads it. Gate on the indicators the active config actually uses |
| B14 | `Strategy.cs:468-472` | the degenerate-stop refusal is **unreachable** — `SeedStop` floors `dist` at one tick (`Exits.cs:188`), so `|TriggerPx − probeStop| >= TickSize` always. Delete it; do not wire B4's callback to it |

---

## 12. `BreakBoxVision.cs` — the visual indicator

An **indicator**, not a strategy. It trades nothing.

**What it draws.** The cloud as a regime-tinted filled region between `eF` and `eS`; the trend line
`eT`; sealed accumulation boxes as white `Draw.Rectangle`; gold / dim-gold signal candles via
`BarBrushes` (§5.3 — Vision is the **sole owner**); entry markers as blue ↑ and exit markers as
magenta ↓, matching §2.2.

**Where its state comes from.** Vision instantiates its **own** `BbCloudState` / `BbEngineState`
from its own parameter set. It is explicitly a **calibration tool, not a mirror** — its parameters
must be set to match the strategy by hand, and a mismatch is *expected to show*. Stating this
prevents the far worse failure of a picture that silently lies about what the strategy is doing.

**Markers come from the §10 JSONL** for the current instrument, not from execution events. No
coupling to a running strategy; it works on a chart with no strategy attached.

**Its job** is to answer the user's original complaint — *"no se parece en nada"* — in an afternoon,
with zero risk and zero Replay sessions spent. He puts it on his MNQ 30s chart and compares
side-by-side against the reference screenshots. Whatever disagrees is a measurement bug found
before it becomes a trading bug.

---

## 13. Calibration protocol — before believing any number

Both adversarial passes converged on this independently. It is house policy for this project.

**Step 1 — count only.** Orders disabled, one counter per gate. At `FlattenHhmm`, one line per
session per engine:
```
CLOUD 2026-08-16 bars=740 regime=310 token=44 reclaim=19 range=17 leg=13 armed=13 filled=9
BOX   2026-08-16 bars=740 cand=88 sealed=31 valid=22 armed=14 filled=9
```
Run 5–10 Replay sessions, **then** tune. Guessing constants and looking at P&L is fitting noise
twice. Log **raw arms** as well as post-cap fills — the capped count is true by construction.

**Step 2 — the random-entry control.** Before any candidate proceeds to evaluation, measure it
against a random entry through the **identical** bracket on `NQData`. Not separable from random →
does not proceed. This is the most valuable methodological artefact the analysis produced and it
applies to every future candidate in this repo.

**Step 3 — the parameter-count problem, stated plainly.** §5.4 has 14 dials and §6.3 has 10. A grid
over even a third of them is tens of thousands of trials, and `[[latigobreak-project]]`'s 104K-trial
lesson applies directly: the statistical bar rises with the trial count. Therefore — **freeze the
M-tagged parameters** (they are measurements, not dials), tune **at most three G parameters** in
step 1, and pre-register the rest at their defaults. Anything beyond that needs the walk-forward
validator that §15 defers, and the honest thing is to say so rather than to sweep.

**Step 4 — pre-register.** Defaults and the kill criterion in writing before the first evaluated
session. **The frequency target is 8–12 fills/session**, chosen so a 100-trade sample accrues in
~10 sessions. Note the tension: §2's single-sample cadence estimate implies ~22 fills over the B8
window. If the counter run lands near 22, either widen the gates' *acceptance* or accept a shorter
calibration — but record which, because a strategy whose realised frequency is 3× off its design
frequency is a **different strategy** and its statistics do not transfer.

Gates: `[[strategy-profitability-gates]]`, out-of-sample only, **never the vendor's numbers**.

---

## 14. Testing

`scripts/check.sh` grows to cover `BreakBoxCloud.cs` and `BreakBoxHistory.cs` in both the net8 test
runner and the concatenated NT8 compilation unit.

**Fidelity tests — one correction required.** `BracketTests.cs::FidelityImage2Splits` currently
asserts a 10-lot at `Tp1Pct = 70` yielding `q[0] == 7` ("observed 7 of 10"). §2.1 refutes that
observation: the trade was **13 contracts, 7/6**. The first draft of this spec called the fidelity
tests untouchable while simultaneously refuting one of them. **Update that case to 13 lots at
`Tp1Pct = 54` → 7/6**, and leave the other two (the 4/2/1 split and TP2 at 23701.25) untouched.
They remain the tripwire for anyone "improving" the bracket.

**New tests.** Cloud regime latch (mint, hold through a zeroed instantaneous regime, kill on `eT`
cross) · token mint/kill/consume/**restore** including the `else if` semantics and the `ext = NaN`
guard · the gold-candle qualifier at each boundary, **both directions** · box formation → seal →
invalidate → cold start · the seconds→bars conversion at 15s/30s/1m/2m plus the tick-bar estimate ·
engine arbitration (both ON, opposite directions, same bar) · a synthetic-session rate assertion on
**raw arms**.

---

## 15. Deliberately not here

- **The walk-forward validator** (their real moat). Deferred by the user.
- **The multi-account cascade.** No account selector — it is meaningless inside a `Strategy`.
- **A performance-based signal selector.** The max-of-N selection arithmetic says no such selector
  ships before the validator exists.
- **The pie chart** (the stacked W/BE/L bar covers it) and the vendor wordmark.

---

## 16. The one open question worth a screenshot

The stop source is **not disambiguated**. The reference's drawn stop is equally consistent with a
`Candle` stop widened by the ATR floor and with a stop at the far ribbon edge (EMA23) — the two sit
**0.19 pts apart**, inside measurement noise. §2 tags this row **S** for that reason.

**One uncropped frame, or one frame with `SL Sig` set to `Swing` / `MA` / `E50`, settles it.**
Worth asking Javier for before writing code that assumes an answer. Until then `Candle` is the
default and `Ma` (with `MaPeriod = RibbonSlow`) is the head-to-head alternative in §13 step 1.
