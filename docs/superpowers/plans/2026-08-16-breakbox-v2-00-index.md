# BreakBox v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace BreakBox v1 — which took zero trades — with a cloud-reclaim signal engine, a
correctly-scaled box engine, a vertical diagnostic panel, and cross-session trade history.

**Architecture:** Two pure signal engines (`BbCloud`, `BbEngine`) return the same `BbAction` and feed
the same already-tested `BbExits` bracket. A thin NinjaScript shell owns orders, session, governor
and the seconds→bars conversion that makes the model bar-size independent. The panel renders a gate
ladder from whichever engine would act next, so "why is it not trading" is answerable at a glance.

**Tech Stack:** C# 7.3 · NinjaTrader 8 (.NET Framework 4.8) · WPF (`UserControlCollection`) ·
a hand-rolled net8 assert runner for the pure files · `nt8c` for the NT8 compile gate.

**Spec:** [`docs/superpowers/specs/2026-08-16-breakbox-v2-design.md`](../specs/2026-08-16-breakbox-v2-design.md)

---

## Global Constraints

Every task's requirements implicitly include this section.

- **Pure files** (`BreakBoxTypes.cs`, `BreakBoxCore.cs`, `BreakBoxCloud.cs`, `BreakBoxExits.cs`,
  `BreakBoxHistory.cs`): **zero `using NinjaTrader.*`**, namespace `BreakBoxCore`, **C# 7.3 only**.
  They compile inside `tests/BreakBox.Tests.csproj`, which has no NT8 assemblies on its reference
  path. A `using NinjaTrader` in one of them is how a CS0101 duplicate-type clash starts.
- **File I/O is not pure.** It lives in `BreakBoxStrategy.cs`. `BreakBoxHistory.cs` holds the record
  type, a hand-rolled serialiser/parser and the equity reduction — nothing that touches a disk.
- **No JSON library.** NT8 is .NET Framework 4.8 / C# 7.3; the test runner is net8. No serializer
  sits on both reference paths. Serialisation is hand-rolled.
- **No horizon is expressed in bars on the `NinjaScriptProperty` surface.** Every one is seconds,
  converted through `BbScale.Bars(secs, barSec, min)` inside `BuildConfigs()`. This is the contract
  that fixes v1's root cause, and `BuildConfigs()` — not `State.DataLoaded` — is where the
  conversion runs, because every panel toggle calls it again.
- **Order APIs only from `OnBarUpdate` or strategy-thread events**, never from `OnMarketData` and
  never from a WPF click handler. Panel actions cross back via `TriggerCustomEvent`.
  See memory `[[nt8-orders-from-marketdata-thread-crash]]`.
- **The order-event race rules the shell.** Every in-flight flag is written *before* its submit;
  every handler clears on the signal **name**. See memory `[[nt8-order-event-race]]`.
- **Tests** are asserts in the existing hand-rolled harness (`tests/Program.cs`, `T.Check`,
  `T.CheckClose`, `T.CheckInt`). **No test framework, no fixtures.** Each new test file registers
  itself by *adding one line* to `Program.Main` — never by replacing the block.
- **The gate is `scripts/check.sh`** and both halves must be clean: `dotnet run --project tests`
  (all asserts pass) and all NT8 files concatenated into one compilation unit checked with `nt8c`
  (0 errors, 0 warnings). `nt8c check` on a *single* file reports false CS0246 on every cross-file
  type — that is expected and is why the gate concatenates.
- **Deploy is part of done.** A task is not complete until the `.cs` files are in
  `…/NinjaTrader 8/bin/Custom/Strategies/`, verified with `cmp`. See memory
  `[[nt8-deploy-copy-files]]`.
- **Commit messages** use a conventional prefix and end with
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- **Clean room.** This rebuild is from black-box observation of a competitor product. No
  decompiling, no reuse of their names, order prefixes (`A_`, `B_`) or branding. **This repo stays
  private.** Order signal names are ours: `BB_CloudLong`, `BB_CloudShort`, `BB_BoxLong`,
  `BB_BoxShort`.
- **Nothing here is validated.** Every default is provisional. No number produced by this code means
  anything until §13 of the spec has been run — a counting pass with orders disabled, then a
  random-entry control.

---

## File Structure

| File | Responsibility | Pure? | Phase |
|---|---|---|---|
| `ninjascript/BreakBoxTypes.cs` | bars, swings, ATR, EMA, rounding, window math, **`BbScale`**, **`BbGateReport`**, **`BbMath.CloseInRange`** | yes | 1 |
| `ninjascript/BreakBoxCloud.cs` | **NEW** — `BbCloudConfig`, `BbCloudState`, `BbCloud`: regime latch, pullback token, gold-candle gates | yes | 2 |
| `ninjascript/BreakBoxCore.cs` | **REWRITTEN** — box lifecycle (form → seal → invalidate), validity against the box-range distribution | yes | 3 |
| `ninjascript/BreakBoxExits.cs` | the bracket — **unchanged**; only its `BreakevenOnTp1` default is discussed | yes | — |
| `ninjascript/BreakBoxHistory.cs` | **NEW** — `BbTradeRecord`, serialise/parse, cumulative equity | yes | 4 |
| `ninjascript/BreakBoxStrategy.cs` | series, indicators, orders, session, governor, scaling, arbitration, history file I/O | no | 1–4 |
| `ninjascript/BreakBoxPanel.cs` | **REWRITTEN** — vertical sidebar, gate ladder, engine log, history chart | no | 4 |
| `ninjascript/BreakBoxVision.cs` | **NEW** — indicator: cloud, boxes, gold candles, markers. Trades nothing | no | 5 |
| `tests/ShellTests.cs` | **NEW** — `BbScale`, `BbGateReport`, the shell-owned pure types | — | 1 |
| `tests/CloudTests.cs` | **NEW** — regime latch, token, gold-candle gates | — | 2 |
| `tests/BoxTests.cs` | **REWRITTEN** — box lifecycle and cold start | — | 3 |
| `tests/HistoryTests.cs` | **NEW** — round-trip, equity, the write guard | — | 4 |
| `tests/BracketTests.cs` | **one case corrected** (13 lots 7/6, not 10 lots 7/3); the other two fidelity tests are untouchable | — | 4 |

---

## Phases

Each phase ends with working, testable software and is a legitimate stopping point.

| # | Plan | Delivers | Tasks |
|---|---|---|---|
| 1 | [`01-scaling-and-shell-fixes`](2026-08-16-breakbox-v2-01-scaling-and-shell-fixes.md) | `BbScale`, `BbGateReport`, and the 14 verified shell fixes. The v1 box engine starts producing boxes | 1–11 |
| 2 | [`02-cloud-engine`](2026-08-16-breakbox-v2-02-cloud-engine.md) | `BreakBoxCloud.cs` + arbitration + deploy. **The primary signal** | 20–28 |
| 3 | [`03-box-engine`](2026-08-16-breakbox-v2-03-box-engine.md) | `BreakBoxCore.cs` rewritten; the slot machinery deleted (closes B11, B12) | 40–49 |
| 4 | [`04-history-and-panel`](2026-08-16-breakbox-v2-04-history-and-panel.md) | Cross-session history + the vertical panel | 60–69 |
| 5 | [`05-vision-indicator`](2026-08-16-breakbox-v2-05-vision-indicator.md) | `BreakBoxVision.cs` — the side-by-side comparison against the reference | 80–87 |

**Phase 1 must land first** — every later phase converts its dials through `BbScale` and writes its
gate report through `BbGateReport`. Phases 2 and 3 are independent of each other. Phase 4 needs 2
and 3 for the gate ladder's row labels. Phase 5 needs 4 for the history-driven markers.

### The order that matters most

If time is short, **Phase 1 → Phase 2 → Phase 5** is the shortest path to answering the question the
user actually asked: *does this look and behave like the product I showed you?* Phase 5's indicator
answers it on a chart in an afternoon, with no risk and no Replay sessions spent.

---

## After the plan

Implementing every task leaves a strategy that compiles, deploys and trades. It leaves **nothing
validated**. Spec §13 is the next piece of work and it is not optional:

1. **Count only** — orders disabled, one counter per gate, 5–10 Replay sessions, *then* tune.
2. **Random-entry control** — on `NQData`, through the identical bracket. Not separable from
   random → does not proceed.
3. **Freeze the M-tagged parameters**, tune at most three G parameters, pre-register the rest.

Gates: `[[strategy-profitability-gates]]`, out-of-sample only, never the vendor's numbers.
