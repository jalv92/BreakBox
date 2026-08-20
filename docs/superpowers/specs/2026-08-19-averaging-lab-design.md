# Averaging Lab — sim-only dynamic averaging-down module (design spec)

**Status:** approved direction (Javier, 2026-08-19) — build as a SIM-ONLY Playback laboratory.
**Host for first test:** BreakBox v2 (`feat/v2-cloud-and-box`).
**Honest-use statement (binding):** the adversarial review (3-agent workflow, 2026-08-19) concluded
that averaging-down cannot create edge (optional-stopping: any bounded add schedule has EV = 0 under
a driftless price, so the achieved win rate equals its own break-even threshold), that the loss cap
is a *mode* not a *maximum* (stop slippage multiplies by the full stack), and that the payoff shape
is the exact one that breaches 4 of 5 prop firms on unrealized drawdown. This module ships anyway,
by explicit decision, as a **laboratory to generate evidence** — the same precedent as LatigoBreak's
honest-use shipping. It must never arm on a live account, and its telemetry (not its P&L) is the
product. Every default is provisional; nothing here is validated.

## 1. What it does

An ON/OFF parameter group ("Averaging") in the host's risk section. When ON and the position goes
adverse, the module adds contracts at pre-planned levels (confirmation-gated, see §3), maintains ONE
stop for the whole stack pinned so the worst-case loss stays inside a dollar budget carved from the
daily loss limit, and maintains ONE take-profit that moves so the trade still exits at the same net
dollar profit `G` originally planned. When OFF, the host runs byte-for-byte as today.

Module-ON and module-OFF are **two different strategies** and validate separately.

## 2. The math

Long side shown; short is mirrored. `v` = $/tick (from `Instrument.MasterInstrument`, never
hard-coded — same L gives d = 12 ticks on NQ and 192 ticks on MNQ), `q` = contracts per add (flat),
`N` = max adds, `d` = spacing in ticks, `s` = stop buffer in ticks below the deepest level,
`c` = round-turn commission per contract, `slip` = slippage reserve in ticks.

**Budget (effective):**

```
L_eff = L_arm − c·q·(N+1) − v·q·(N+1)·slip
```

**Envelope (planned geometry, solved at arm time):**

```
L_eff = v·q·(N+1)·(N·d/2 + s)      →      d = (2/N)·(L_eff/(v·q·(N+1)) − s)
```

**Hard constraints at arm — refuse to arm (loud print naming the binding constraint) if any fails:**
- `d ≥ 1 tick` after solving (else the budget cannot host the grid at this q/N/s).
- `s ≥ d/2` (guarantees the loss curve is monotone in fills — a partial grid always loses less
  than L_eff under ANY fill subset, proven, not assumed).
- `G ≥ Q_N·(TP_FLOOR_TICKS·v − c)` with `TP_FLOOR_TICKS = 8` — the TP distance per the full stack
  never collapses under spread+queue+commission (the LatigoBreak fixed-dollar-threshold pathology).

**Planned levels:** `ℓ_k = P0 − k·d` for k = 1..N. **Planned stop:** `S = ℓ_N − s`.

**Runtime-authoritative recomputation (this, not the plan, is the guarantee):** confirmation-gated
adds fill ABOVE their level, so planned geometry understates the real loss. After **every** fill,
recompute from live `Position.AveragePrice` (= `A`) and `Position.Quantity` (= `Q`):

```
S_live = max( S_planned , A − L_eff/(Q·v) )        // stop may only rise, never fall
TP     = A + (G + c·Q)/(Q·v)                        // in ticks above the live average
```

Any remaining add level at or below `S_live + 1 tick` is marked dead. This keeps `worst case ≤ L_arm`
(commissions and the slippage reserve included) under any fill prices, partial fills, or rejections.

**Viability, for the record:** the trade wins iff price retraces to TP before S. Break-even
`p* = (L_eff + costs)/(G + L_eff)` ≈ 91.5% at L=$1,000/G=$100 — and p* is also the *null* value of p.
The lab exists to measure whether confirmation-gating pushes p above it. Expected: it does not.

## 3. Design decisions (locked)

| Decision | Choice | Why |
|---|---|---|
| Max adds `N` | dial, **1..32**, default 2 | originally clamped to 2: all of the benefit is at the first add, N≥3 only levers the tail, and Lucid-50K arithmetic admits at most N=2 anyway. **User override, 2026-08-20: the user sets the depth; the budget envelope, not a clamp, is the restraint** — a big N against a fixed budget shrinks `d` or refuses to arm on its own |
| Sizing | **flat** q per add, never geometric | under a fixed budget, back-loading is a martingale generator; flat keeps the envelope exact and the tail linear |
| Add trigger | **confirmation-gated**: level touched, then a bar CLOSES back on the favorable side of it → market add, once per level | the only design with a route to p > p*: on a straight-line adverse move it never fires, so size correlates negatively with trend strength. Blind resting limits are the placebo, not the product |
| Spacing `d` | `min(d_structural, d_max_from_L)`, frozen at arm | L is the hard ceiling; structure only informs. Structural source: box height for box-engine trades, `AtrMult × ATR` for cloud-engine trades (Auto) |
| Stop | static planned bottom, raised only by the runtime recomputation | "stop where the current position loses L" gives a 200-tick stop at k=0 — rejected |
| Breakeven | **User request, 2026-08-20:** percent-of-TP breakeven owned by this module alone (`BreakevenEnabled`/`BreakevenPct`/`BreakevenOffsetTicks`, default on/50%/5t). Once the bar extreme has covered `BreakevenPct` of the **average → TP** span, the whole-stack stop moves to `average + offset` in our favour. One-shot; one-way (never behind the budget ratchet); every level it overtakes dies | breakeven on an averaging stack has to mean the LIVE AVERAGE, because the average is the cost basis and the dynamic TP already hangs off it — from the entry, a dug grid's progress reads negative for most of its life. The existing tier breakeven (`BreakevenOnTp1`) is untouched and stays inert here (`Tiers == 0`, so `OnTierFill` never runs). Killing the overtaken levels is the §2 envelope invariant, not a preference: an add below the stop is a fill the envelope never priced. The consequence is intended — after BE the remaining grid is disarmed, because every add level sits below the BE stop |
| Vol abort | one-way: current ATR > `VolAbortMult ×` entry-ATR snapshot → kill remaining levels, keep position + stop | frozen spacing + vol expansion = max size in ninety seconds; adaptivity may only ever reduce exposure |
| Daily budget | `L_arm = min(BudgetDollars > 0 ? BudgetDollars : BudgetFraction × DailyLossLimit, DailyLossLimit − day loss so far)`, via the host's existing `_dayPnl`/`CheckDailyLimits` machinery; **no replenishment from wins**; one armed trade at a time. **User override, 2026-08-20:** `AveragingBudgetDollars` (0 = off) sets the per-trade budget directly, bypassing the fraction; either way the day-left cap binds, so one trade may never out-risk the remaining day — a direct budget that gets capped prints once, naming the cap | the fraction split the budget across two parameter groups (DailyLossLimit lives in "06. Session") and was hard to find; a direct dollar dial puts the number where the user is already looking. A later trade in a losing day still gets a smaller grid automatically; host lockout semantics unchanged |
| Session close | no arming within `NoAddsFinalMinutes`, no adds after that cutoff; the host's exit-on-close (30 s) flatten of a dug grid is a logged third outcome, not a bug | `IsExitOnSessionCloseStrategy = true` already (BreakBoxStrategy.cs:218-219) |
| Bracket handoff | module ON → the 3-tier bracket (`SplitTiers`/BE/trail) is **never armed** for that trade; single explicit branch at the top of `OpenBracket()` | the tiered R-multiple bracket and a grid stop are incompatible models of one trade; two writers on one stop field is the failure mode |
| ATM mode | refuse to arm (one-line guard) | ATM position getters are async/stale; ATM already owns a stop |
| Restart mid-grid | if a position exists but no in-memory grid state → flatten with a loud print. No recovery file in the lab | safe fallback; the recovery-file upgrade path is noted in §8 |

## 4. Sim-only guard (non-negotiable)

At `State.Realtime`, if the connected account name does not start with `Sim` or `Playback`, the
module force-disables itself (`AveragingEnabled` treated as false) and prints once, loudly.
Historical/Analyzer runs are inherently sim and are allowed. There is no override dial.

## 5. Architecture

**New file `ninjascript/AveragingEngineCore.cs`** — pure decision code, zero `using NinjaTrader.*`,
C# 7.3, same convention as `BreakBoxCore.cs`/`BreakBoxExits.cs` (header rule: "it never touches an
order"). Compiles into the shared Custom assembly, so any future strategy composes it. Two entry
points:

```
Arm(cfg, dir, entryFillPx, initialQty, tickValue, dStructuralTicks)
    → AvgPlan { LevelPx[], StopPx, TpPx, DTicks, LEff, Refused, Why }
OnFill(plan, liveAvgPx, liveQty)
    → AvgUpdate { StopPx, TpPx, DeadLevels[] }
```

Plus small pure helpers for the confirmation state machine (touched → confirmed, once per level)
and the vol-abort test. Unit-tested in `scripts/check.sh`'s pure half with asserts, like BbExits.

**Host wiring (`BreakBoxStrategy.cs`), verified anchors:**

1. `OpenBracket(fillPx, qty)` (line 1102): `if (AveragingEnabled && armable) → averaging path`,
   else the existing tier path. `SubmitTier` (1151) is never invoked on averaging trades.
2. `EntriesPerDirection = 1` (line 216) becomes `AveragingEnabled ? AveragingMaxAdds + 1 : 1`,
   set in `State.Configure` (where property values are settled), NOT in `SetDefaults`. Without this
   NT8 silently ignores every add — the module would look armed and do nothing.
3. Adds submitted as **market orders on confirmation**, each with its own signal name
   (`BB_Long_Add1`, `BB_Long_Add2`). Stop and TP are each ONE order covering the whole stack,
   `ExitLongStopMarket`/`ExitLongLimit` with `fromEntrySignal = ""` (attach-to-all semantics),
   live-until-cancelled — the LatigoBreak v3 pattern, no `Set*` anywhere on this path.
4. Resize on each add fill reuses the existing tier-resize idiom (lines 1299-1324: resubmit at new
   quantity, `_lastStopSent = NaN` to defeat the dedupe) and the existing
   `_stopChangePending`/watchdog (86, 676-689). Order-reference anti-echo pattern ported verbatim
   from LatigoBreak commit `575c524`: null the tracked ref BEFORE each resubmit; ignore events whose
   Order ref doesn't match; defer any "cancelled by hand" verdict by `BracketCancelGraceSec`.
   In-flight flags set BEFORE every submit (house rule, nt8-order-event-race).
5. **Rejection fork:** a rejected ADD logs and marks that level dead — it must NOT route through the
   existing "any leg rejected → FlattenAll" branch (1367-1389). A rejected protective order keeps
   routing there. This fork matters because `RealtimeErrorHandling = IgnoreAllErrors` (line 226)
   swallows rejections today; the averaging path handles rejections explicitly via `OnOrderUpdate`.
6. Daily budget reads the existing `_dayPnl` / `CheckDailyLimits` (1485-1505); no parallel counter.

## 6. Telemetry — the actual product

One JSONL record per armed trade (`BreakBoxHistory.cs` UserDataDir append pattern, file
`averaging_lab_log.jsonl`): arm inputs and solved geometry (P0, dir, q, N, d, s, S, G, L_arm, L_eff,
spacing source, engine), every fill (level, planned px, actual px, qty, confirm-bar time), every
state event (level dead, vol abort, stop raise), outcome (`tp | stop | session_flatten | other`
— a vol abort is not an outcome: it kills the remaining adds and leaves the position and its
stop alone, so it is recorded in `addsAborted`/`abortWhy` and the trade still ends tp/stop/flatten),
realized P&L, **minimum unrealized equity during the trade** (the prop-firm axis), and a compact
per-bar path `[t, h, l, c]` from entry to exit so ANY counterfactual (flat q with the host's own
3-tier bracket — the real competitor) is computable offline without re-running Playback.

## 7. Parameter surface (GroupName "Averaging", after the existing Parameters region)

| Dial | Type / default | Meaning |
|---|---|---|
| `AveragingEnabled` | bool, **false** | master switch |
| `AveragingMaxAdds` | int 1..32, default 2 | N — commissions, tail overshoot, and the required sample all scale with N; the lab measures it either way |
| `AveragingAddQty` | int, default = base quantity | q |
| `AveragingBudgetDollars` | double 0..1,000,000, default 0 | direct $ budget for one averaging trade; 0 = derive from `AveragingBudgetFraction` instead. Either way capped by what is LEFT of today's daily loss limit — one trade may never out-risk the day |
| `AveragingBudgetFraction` | 0..1, default 0.5 | share of DailyLossLimit one trade may risk, used only when `AveragingBudgetDollars` = 0 |
| `AveragingTargetProfitDollars` | double, default 150 | G — explicit, never inferred from Tp1R. Default is 150, not 100, because the §2 TP-floor constraint needs G ≥ Q_N·(8v − c) = $102.72 on NQ at q=1, N=2 — a $100 default would refuse to arm out of the box |
| `AveragingBreakevenEnabled` | bool, **true** | the module's own breakeven; independent of `BreakevenOnTp1`, which stays inert on averaging trades |
| `AveragingBreakevenPct` | double 1..99, default **50** | percent of the **average → TP** distance, measured on the bar extreme (high long / low short) at bar close |
| `AveragingBreakevenOffsetTicks` | int 0..500, default **5** | the BE stop parks this far BEYOND the live average, in our favour — it locks a small profit rather than scratching. Firing BE kills every remaining add level |
| `AveragingStopBufferTicks` | int, default 8 | s (raised to d/2 at arm if below) |
| `AveragingSpacingSource` | enum Auto \| BoxHeight \| AtrMult, default Auto | d_structural source |
| `AveragingSpacingAtrMult` | double, default 1.0 | when ATR-based |
| `AveragingConfirmBars` | int, default 1 | closes back beyond the level required to add |
| `AveragingVolAbortMult` | double, default 2.0 | one-way abort threshold |
| `AveragingNoAddsFinalMinutes` | int, default 15 | pre-close cutoff for arming/adding |
| `AveragingCommissionRt` | double, default 5.76 | c, per contract round-turn |
| `AveragingSlippageReserveTicks` | int, default 2 | reserve subtracted from the budget |

## 8. Validation protocol and kill criteria (pre-registered, before any Playback session)

**The breakeven dial changes the experiment, and honestly:** a BE'd trade can no longer reach the
module's own TP-or-stop dichotomy — it exits at the average plus a few ticks, which is neither the
`G` the envelope was solved for nor the `L_eff` it was priced against. So outcome accounting gains a
third shape, and the log's `beApplied` is the only thing that separates it from a real tp/stop.
Split the corpus on that flag before computing `p`: mixing BE'd trades into the win column inflates
`p` while shrinking the realized `G` that `p*` is computed from, which moves the kill threshold and
the measurement in the same direction and would flatter the module twice over. With `AveragingBreakevenEnabled = false`
the module measures the original null; the honest comparison is the two arms side by side, not one blended curve.

- **Primary measurement:** empirical `p = P(TP before S)` per armed trade from the lab log, with a
  95% lower confidence bound, clustered by session. **Kill:** lower bound < p* computed from the
  same log's realized G/L_eff — the module dies and the log is archived as the negative result.
- **Competitor arm (offline, from the logged bar paths):** flat q at P0 with the host's own 3-tier
  bracket at the same total risk. **Kill:** module net P&L ≤ competitor net P&L over the corpus.
- **Placebo (offline):** the identical grid replayed on direction/time-matched random entries. If
  the random-entry curve shows the same win rate, the curve is an artifact of the payoff shape —
  report it as such regardless of P&L.
- **Sample honesty:** at G=$100 / L≈$1,000 the payoff needs ≈680 trades for a 95%/80% claim; the
  house ≥100-trade minimum does NOT apply — early positive curves are expected and mean nothing
  (first full-grid loss arrives around trade 14 at p=0.93). No promotion decision of any kind
  before the pre-registered n; this sentence is the promotion gate.
- Fill realism: Playback with the module live; any Analyzer cross-check at High + Tick only.

**Upgrade paths (explicitly out of scope for the lab):** recovery file for mid-grid restarts;
additive (non-cancel-replace) stop resizing for live; cross-class envelope registry in a shared
AddOn; prop-account headroom recomputation via PropGuard. None get built unless the lab survives §8.
