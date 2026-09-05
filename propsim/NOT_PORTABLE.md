# BreakBoxStrategy (BreakBox v2) -- not portable to PropSim

`ninjascript/BreakBoxStrategy.cs` (+ `BreakBoxCore.cs`, `BreakBoxCloud.cs`,
`BreakBoxExits.cs`) is BreakBox v2, a separate `Strategy` class from
`TrendST.cs` (ported as `trend_st.py` in this same folder). It is **not**
ported here: its own default configuration needs a bracket shape the PropSim
engine's contract cannot represent, not merely one this port chose to skip.

## The blocker

`engine.py`'s `Strategy.entries()` contract is one stop price and one target
price per trade (`engine.resolve(tape, entry_idx, direc, stop, target, ...)`,
`engine.py:1085`) -- optionally a resting limit price and a single breakeven
trigger price. There is no representation anywhere in the engine for a
position that exits in pieces at different prices.

BreakBoxStrategy's own bracket (`BreakBoxExits.cs`, `BbExitConfig`/`BbExits`)
is the opposite of that by design: `TierCount = 3` targets, each an
independent R-multiple (`Tp1R=0.5`, `Tp2R=1.0`, `Tp3R=1.5`) closing a
different **partial quantity** of the position (`Tp1Pct=50`, `Tp2Pct=30`,
remainder 20% -- the observed 4/2/1 split on a 7-lot), with breakeven armed
specifically by the TP1 **fill** and a chandelier ATR trail armed specifically
by the TP2 **fill**. `BaseQuantity = 3` by default, so even the smallest
default trade needs a real multi-contract split. This bracket geometry is the
strategy's whole reason to exist (`BreakBoxExits.cs`'s header: "reverse-
engineered from black-box observation of a competitor product... clean room,
from screenshots"), not an optional flourish -- it is live at the class's own
defaults, no non-default toggle needed to reach it.

Collapsing three partial exits into one scalar target (or picking one tier,
or a quantity-weighted blended price) is not this strategy: a trade that
banks 50% at +0.5R and lets the rest run to +1.5R has a fundamentally
different P&L distribution than one target at any single R-multiple, and the
breakeven/trail arm at different, fill-triggered moments that a single-target
model cannot reach at all. This is exactly the failure this codebase's own
house rule elsewhere warns against -- an approximation that makes the
backtest and the live strategy "two different experiments" silently (see
`pullback_zone.py`'s own docstring on its closed parameter list, and memory
`escalate-to-research-after-two-fails` / `feedback-audit-bug-class-not-
instance` on not patching around a structural mismatch).

## Compounding, not load-bearing on their own

Even if the engine supported multi-tier exits, two more things would need
porting before a run meant the same strategy:

- **Two engines, one position.** `EnableBreak = true` AND `EnableCloud = true`
  both by default (`BreakBoxStrategy.cs` SetDefaults) -- the box (`BbEngine`,
  `BreakBoxCore.cs`) and the cloud (`BbCloud`, `BreakBoxCloud.cs`) both look
  for entries every bar, arbitrated in the shell's Bar loop (not read in
  detail here, since the tiered-exit blocker alone is sufficient to stop).
- **Averaging Lab.** `AveragingEnabled = false` by default, so it does not
  block a default-config port on its own -- but it is the other half of what
  makes this class "v2", and is itself SIM-only telemetry per its own
  comments, a further sign this file is not a single, simply-portable
  strategy.

`StopSourceParam = Candle` by default, so the Swing/MA/EMA50 stop sources
(needing a swing detector or moving averages beyond what `TrendST` already
uses) are NOT a blocker at the default configuration -- noted only so a
future attempt does not have to rediscover that this part is fine.

## What would have to change for this to become portable

The PropSim engine's `resolve()` would need a genuine multi-tier partial-exit
primitive (N target prices with N partial quantities, tier-triggered
breakeven and trail) before a faithful `break_box.py` could be written. That
is an `engine.py` change, out of scope for a plugin file, and not something
this task's brief ("do NOT tune anything; the lead runs the evaluation")
authorizes deciding alone.
