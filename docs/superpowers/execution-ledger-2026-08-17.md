# SDD ledger — plan: docs/superpowers/plans/2026-08-16-breakbox-v2-PLAN.md

**Spec:** `docs/superpowers/specs/2026-08-16-breakbox-v2-design.md` (read; it is the binding authority)
**Branch:** `feat/v2-cloud-and-box` (created off `main` at `3c50989`)
**Started:** 2026-08-17

## Plan shape

| Phase | File | Tasks |
|---|---|---|
| 1 | `…-01-scaling-and-shell-fixes.md` | 1–10 (11 = tombstone, folded into Phase 4) |
| 2 | `…-02-cloud-engine.md` | 20–28 |
| 3 | `…-03-box-engine.md` | 40–49 |
| 4 | `…-04-history-and-panel.md` | 60–69 |
| 5 | `…-05-vision-indicator.md` | 81–87 (80 moved to Phase 1) |

Task numbers are globally unique and deliberately gapped. **Never renumber** — every
cross-reference between phase files is by task number.

## Pre-flight conflict scan

The scan was run as a dedicated adversarial pass (4 lenses over the spec, then 1 cross-phase
checker over all five drafts) rather than by hand. It produced **28 concrete discrepancies**, each
with an authoritative correction, recorded at `docs/superpowers/plans/_consistency-report.md`. All
28 were applied by a per-file correction pass before execution began.

### Task pairs sharing a file or an interface — findings and rulings

| Pair | Shared surface | Finding | Ruling |
|---|---|---|---|
| T1 ↔ T48 | `BbScale` | defined twice, contradictory `barSec < 1` semantics | T1 owns it; T48's copy deleted, its assert re-pointed. **Applied** |
| T7 ↔ T27 ↔ T49 | `_owningEngine` | declared three times → CS0102 | T7 owns the declaration; others assign only. **Applied** |
| T9 ↔ T40 | `MaxTradesPerDay` | 30 vs 20 — T40 silently reverts B9 | 30 (B9's value). **Applied** |
| T3 ↔ T40 ↔ T27 | `TriggerLifeBars` / `TriggerLife` | three names for one field | `TriggerLife` from T3 onward. **Applied** |
| T7 ↔ T40 | `BbEntryEngine.Cloud` | used in Phase 1, only added in Phase 3 → CS0117 | T7 widens the enum. **Applied** |
| T63/T67 ↔ T40 | `_uiBoxOn` | consumed, never produced | renamed to `_uiBreakOn`. **Applied** |
| T26 ↔ T82 | `BbCloudState.SlopeBuf` | never sized on the strategy path → NRE on first bar | sized by `BbCloud`'s constructor. **Applied** |
| T40 ↔ T85 | `BbBox.AnchorStart` | deleted by T40, still used by T85 | T85 uses `SealedAt`. **Applied** |
| T5 ↔ T27 | `BbEngine.OnBar` arity | T27 consumes the 6-arg signature T5 replaced | 7-arg contract. **Applied** |
| T7 ↔ T27 ↔ T49 | shell refusal plumbing | written three times, three different reason strings | T7 is sole owner. **Applied** |
| T40/T46 ↔ T65 | gate ladder depths | engine emits 10 depths, panel renders 6 → panel paints green while blocked | `GateRows = 10`, labels copied from the engine's own strings. **Applied** |
| T1 ↔ T60 ↔ T80 | `Program.Main` | each replaces the run list, dropping the others' registrations | each **inserts one line**. **Applied** |
| T11 ↔ T67 | B5 panel-thread fix | fixed twice under two names | T11 deleted (tombstone), T67 owns it as `Rebuild()`. **Applied** |
| T5/T6 ↔ T40 | `tests/BoxTests.cs` | T40's full rewrite discards T5/T6's B2/B6 asserts | asserts move to `ShellTests.cs`. **Applied** |
| T1/T2/T3 ↔ T81 | `BarSeconds()` | implemented twice with different constants | T81 calls `BbScale`. **Applied** |
| T80 (Phase 5) → Phase 1 | `BbMath.CloseInRange` | Phase 2 would hand-roll the ratio Phase 5 exists to share | moved into Phase 1. **Applied and verified** |
| T46 ↔ T47 | `check.sh` red between tasks | T46 deliberately leaves the gate red and skips its commit | expiry asserts moved into T47. **Applied** |

### Per-task self-agreement

Every task's own text was checked by its phase's correction agent against the files it creates
versus the files it later touches, and the tests it specifies against the code it specifies. Two
tasks failed and were rewritten: **T49** (its "verify it fails" preconditions were already satisfied
by T7 and T8, so its Step 2 could never fail) and **T6** (its comment promised a bounded restore on
expiry that T47 explicitly refuses to implement).

**Ruling:** the spec's §11 bug table was itself wrong in two entries (B2 and B6) and was corrected
against the files on disk before the plan was drafted. Cost if wrong: implementers would chase a
defect that does not exist and leave the real one. Verified by reading `BreakBoxStrategy.cs:383-396`
and both `TriggerLifeBars` sites.

## Progress


### Rulings made before/while executing

**Ruling: Phase 2 was regenerated from scratch.** Its first author hit an output limit and returned
only its tail — Tasks 27–28 — silently dropping Tasks 20–26, the cloud engine itself. Detected
because the file came back at 15 KB against 51–95 KB for its siblings. Re-run split across two
authors (20–23, 24–26). *Cost if wrong: none — the alternative was executing a plan with its
central phase missing.*

**Ruling: Task 80's body was recovered and placed by hand.** Correction item 15 moved
`BbMath.CloseInRange` from Phase 5 into Phase 1; the Phase 5 agent deleted it and the Phase 1 agent
correctly refused to author another phase's task, so only an anchor note survived. Recovered from
the original draft and placed between Task 4 and Task 5. *Cost if wrong: the cloud engine and Vision
each hand-roll the close-in-range ratio and drift apart — the exact drift item 15 existed to
prevent.*

**Ruling: Task 5's migration regex was widened.** `s/, (atr|[0-9]+\.[0-9]+), true, false\)/…/`
cannot match a call site written with an integer ATR literal (`, 4, true, false)`), and `sed`
reports success either way. Widened to `[0-9]+(\.[0-9]+)?` and followed by
`! grep -q ', true, false)'` so an unmigrated site fails the chain instead of passing silently.
*Cost if wrong: nothing — the guard is strictly additive.*

**Ruling: `TriggerLifeBars` → `TriggerLife` is renamed in Task 3, not Task 40.** The Phase 1 agent
found that `_cfg.TriggerLife` does not compile in Phase 1 unless `BbConfig`'s field is renamed with
it, and pulled a Phase-3-flavoured rename forward. Accepted. **Phase 3's T40 must find the field
already renamed.** *Cost if wrong: T40 attempts a rename that is already done and its grep gate
reads zero where it expected hits — visible immediately, cheap to fix.*

### Contracts published during execution (carry into later dispatches)

- **Cloud gate ladder** (Task 20 Produces): depth 0 `warmup` · 1 `regime` · 2 `token` ·
  3 `pullback` · 4 `cooldown` · 5 `reclaim` · 6 `direction` · 7 `body` · 8 `range` · 9 `leg`.
  `suppressed` is written at depth 3 and is NOT a ladder row.
- **Box gate ladder** (Task 46 Produces): 10 rungs, different names.
- **Consequence for T65:** the panel needs BOTH ladders, `GateRows = 10`, and must select by the
  active engine (§4.2). It may not copy either list by hand — it derives from the engines' own
  `GateLadder` arrays.

## Progress

Task 1: complete pending review (commits 3c50989..eead359)
Task 2: complete pending review (commits eead359..584f457, 114 asserts pass)

**Ruling: tasks are dispatched in batches of 3–4 same-shape tasks, reviewed as one diff.**
47 tasks at one dispatch + one review each is ~94 seats. The skill sanctions batching for
same-shape work; batches are kept to 3–4 so no author hits the output limit that silently
truncated Phase 2's first draft. Batch boundaries follow the plan's own coupling:
{3,4,80} foundation additions · {5,6,7} shell surgery · {8,9,10} mechanical fixes ·
{20–23} cloud core · {24–26} gates+wiring · {27,28} arbitration+deploy ·
{40–43} {44–47} {48,49} box · {60–63} history {64–66} {67–69} panel ·
{81–84} {85–87} vision.
*Cost if wrong: a batch's review finds a fault in one task and the fix round re-touches its
siblings — visible in the diff, cheap to unwind.*
Tasks 3, 4, 80: complete pending review (commits 584f457..d77b80d, 128 asserts pass)
  minor (deferred): task-3 brief says "eight hits" for the TriggerLifeBars grep but lists 7; the
  pre-edit grep found exactly 7. Brief text defect, no missing call site.

Task 1: complete (commits 3c50989..eead359, review clean — spec ✅, quality approved)
  minor (deferred): the `min < 1` clamp in BbScale.Bars is implemented but never exercised
  minor (deferred): implementer wrote test+impl together, skipping the brief's explicit
    watch-it-fail step. Outcome verified correct.
Task 2: complete (commits eead359..584f457, review clean — spec ✅, quality approved)
  minor (deferred): no assert at the exact `count == MinEstimateSamples` boundary (199 and 400
    are exercised, 200 is not)

⚠️ RESOLVED by controller (raised by the Task 1+2 review, unverifiable from that diff): the
reviewer could not confirm that a caller treats `EstimateBarSeconds() == 0` as "fall back AND say
so", because no caller existed in that diff. Task 3 supplies it — `BreakBoxStrategy.cs:377-381`
sets `_barSecLabel = "fallback"` and Prints that every seconds-based horizon is now a guess, and
`:249` surfaces the resolved bar size. Requirement met; not a gap.
Task 3: complete (commit 8258d38, review clean — spec ✅, quality approved, zero findings)
Task 4: complete (commit e8bb1b9, review clean — spec ✅, quality approved, zero findings)
Task 80: complete (commit d77b80d, review clean — spec ✅, quality approved, zero findings)

**Carried forward — verify in Phase 3:** the Task 3 review noted that `RetraceMaxBars` is still a
BAR-valued dial on the NinjaScriptProperty surface, which violates the §8 contract. It was
correctly out of scope for Task 3. Phase 3 (§6.4) retires the Retrace engine and deletes the slot
machinery; **confirm `RetraceMaxBars` goes with it**, or convert it to seconds. If Phase 3 leaves a
bar-valued dial exposed, the §8 contract is not actually true and the final review must catch it.
Tasks 5, 6, 7: complete pending review (commits d77b80d..ac73b06, 142 asserts pass)
  Ruling: `MirrorEngineDisarm()` is named in Task 7's Produces block but never defined — the
  mirroring logic lives inline in AgeWorkingEntry. Grepped the whole plan: the only two hits are in
  Task 7's own Produces block; no later task consumes the name. Naming artifact, no gap.
  *Cost if wrong: a later phase calls a method that does not exist — a compile error, caught
  immediately.*
  minor (deferred): Task 7's Step 4 gate expects `grep -cE 'OnEntryRejected\('` == 4; the brief's
  own Step 3 code produces 5 (the shell router's call to the engine's same-named callback was not
  counted). Brief text defect.
Tasks 8, 9, 10: complete pending review (commits ac73b06..bad99d6, 160 asserts pass)

=== PHASE 1 COMPLETE ===
Full gate run by the controller at this boundary: `scripts/check.sh` green on BOTH halves —
160 asserts pass AND all five NT8 files compile clean as one unit (0 errors, 0 warnings).
This is the first point at which the rebuilt strategy compiles in NinjaTrader.
Task 5: complete (commit 4dccaad, review clean — spec ✅, quality approved, zero findings)
Task 6: complete (commit fce8ee8, review clean — spec ✅, quality approved, zero findings)
Task 7: complete (commit ac73b06, review clean — spec ✅, one Minor)
  minor (RESOLVED, not deferred): the review found a 4th silent-return path — SubmitEntry's
  degenerate-stop guard printed and returned without calling OnEntryRejected, leaving the engine
  armed on a trade nobody submitted. It independently confirmed the branch is unreachable
  (BbExits.SeedStop floors dist at one tick, so |TriggerPx − probeStop| >= TickSize always).
  **Task 8 deleted it** (commit d1edae5); controller verified by grep that no `degenerate`/
  `probeStop` reference survives. The finding is closed by code already on the branch, not deferred.

Notable verification quality: this reviewer traced `_entryBarsWaiting` across all six of its uses to
prove it is a HUD counter never read in a conditional — i.e. that exactly ONE decisional trigger
clock survives — rather than taking the report's word for it.
Task 8: complete (commit d1edae5, review clean — spec ✅, quality approved, zero findings)
Task 9: complete (commit 5076b1b, review clean — spec ✅, quality approved, zero findings)
Task 10: complete (commit bad99d6, review clean — spec ✅, quality approved, zero findings)

=== PHASE 1 CLOSED: 10/10 tasks implemented AND reviewed clean ===
Zero open findings. One Minor was found and closed by code already on the branch (Task 7's 4th
silent-return path, deleted by Task 8). Six minors deferred, all cosmetic or brief-text defects —
listed above for the final whole-branch review to triage.

The Task 10 review chased the one case I flagged as genuinely risky — switching the stop source
mid-session to one whose indicator had never been fed — and proved it safe: `_ma` and `_e50` are
constructed once at DataLoaded and `Update()`d unconditionally every bar regardless of which source
is selected, so warmth is decoupled from selection. `BuildConfigs()` replaces only `_exitCfg`,
never the indicators. There is no path that reads a cold EMA.

=== PHASE 2 — cloud engine ===
Tasks 20, 21: complete pending review (commits bad99d6..77c1efe, 179 asserts, check.sh green both
  halves). The critical latch assert — token survives a deep pullback that zeroes the instantaneous
  regime — passes unweakened per the implementer.
  FLAGGED TO REVIEWER: the implementer changed a loop bound in Task 20's brief-supplied warm-up
  test (`i <= 5` → `i <= 4`), claiming a brief off-by-one. That is exactly the shape a weakened
  test takes, so the reviewer was told to verify the arithmetic itself and call it either a genuine
  brief defect or a Critical finding. Awaiting that verdict.
Task 20: complete (commit 86c4ab7, review clean — spec ✅, quality approved, zero findings)
Task 21: complete (commit 77c1efe, review clean — spec ✅, quality approved, zero findings)

The flagged loop-bound edit is CLEARED. The reviewer counted the pushes itself and found the
brief's original `i <= 5` filled the 6-slot ring BEFORE the "still warmup" assert — i.e. the
original bound made the test **fail**, not pass-weakly. The `i <= 4` fix is a genuine
brief-arithmetic correction and the brief's own "Sixth push fills a 6-slot ring" comment is only
true under it. Nothing about what is verified changed.

It also confirmed the latch test genuinely zeroes the INSTANTANEOUS regime (eF < eS makes the +1
branch fail; close > eT makes -1 fail; now == 0) while RegimeLatched stays +1 — so the assert is
real, not vacuous — and pinned all three clear conditions at their exact boundaries (still latched
at age == RegimeMemory, clears at age == RegimeMemory + 1).

⚠️ CARRIED FORWARD (raised by this review, unverifiable in this diff): "steps 3–5 must read
RegimeLatched and never regimeNow" cannot be checked until mint/kill/trigger exist. Those are
Tasks 22, 23 and 24. **The reviewer of that batch must verify it** — an instantaneous read in the
mint/kill/trigger path reintroduces the zero-trade defect the latch exists to prevent.
Tasks 22, 23: complete pending review (commits 77c1efe..fff259a, 210 asserts, check.sh green)

**Ruling: Task 23's two shell edits are folded into Task 26.** Task 23's brief asked the shell to
call `_cloud.OnEntryRejected` / `_cloud.OnTriggerExpired`, but `_cloud` is declared by Task 26 (its
sole declaration site, same contract as Task 7 owning `_owningEngine`). Applying them in Task 23 =
CS0103 in the combined compile; declaring `_cloud` early = CS0102 when Task 26 runs. The implementer
refused to implement speculatively and escalated — correct. `task-26-brief.md` amended with both
edits verbatim plus the reason.
*Cost if wrong: if Task 26 ships without them, an expired or rejected cloud entry burns the setup —
the exact v1 behaviour Task 23 exists to remove, making Task 23 cosmetic. The amendment says so in
those words, and Task 26's dispatch will repeat it.*

**Ruling: Task 40's brief deletes properties by LINE NUMBER and its numbers are ~120 lines stale.**
It names `:856-862`, `:868-882`, `:888-890`, `:900-922`; the real blocks now sit at `:976-1128`
because Phase 1 added properties to the same file. Deleting by the stale numbers removes the wrong
properties and still compiles — the worst failure mode available. Amended `task-40-brief.md` to
delete **by name**, and told the implementer every other line number in that brief is equally
suspect.

**Same amendment closes the carried-forward `RetraceMaxBars` item.** No Phase 3 brief mentioned it
and Task 40's verification grep did not cover it, so the retired Retrace engine's dials —
`EnableRetrace`, `ExtensionAtr`, `RetraceMaxBars`, `RetraceOffsetTicks` — would have survived. That
matters because `RetraceMaxBars` is a **bar-valued dial on the parameter surface**, which §8 forbids
outright; had it survived, "no horizon is ever expressed in bars" would simply be false. The grep is
now extended to prove all four are gone, while `BbEntryEngine.Retrace` itself survives (§4.1 keeps
the enum value so saved workspaces do not shift).
*Cost if wrong: deleting a dial something still reads is a compile error, caught by check.sh in the
same task.*
Task 22: complete (commit cf76b6d, review clean — spec ✅, quality approved, one Minor)
Task 23: complete (commit fff259a, review clean — spec ✅, quality approved, zero findings)

⚠️ CARRIED-FORWARD ITEM FROM THE PREVIOUS REVIEW IS NOW CLEARED. The reviewer grepped every read
in the mint/kill/trigger/restore paths: all use `_st.RegimeLatched`. The instantaneous `now` is a
local inside `UpdateRegime` and is never read outside it. The zero-trade defect class is closed for
these paths.

  minor (deferred): step 4's `KillToken("closed through E50")` branch is **dead code**. Its
  predicate is the literal same boolean formula as `UpdateRegime`'s `closedThrough`, over the same
  `RegimeLatched`/`eT` — and `UpdateRegime` runs first, so the regime always clears one statement
  earlier and `OnBar` returns "regime"/depth 1 before step 4 can fire. The token still dies
  correctly either way, so this is not a functional bug. **But the kill reason "closed through E50"
  can never surface to an operator** — and Phase 4's whole purpose is a panel that explains why the
  strategy is not trading. A ladder detail string that is unreachable is a small lie in exactly the
  component built to stop lying. Flag for the final review: either delete the branch or collapse the
  two into one reported reason.

**Ruling: Tasks 27 and 28 never received the cross-phase corrections** — they were preserved
verbatim from the pre-correction Phase 2 draft when I rebuilt that file, so the correction pass
never reached them. Both amended before dispatch:

- **Task 27** carried three hard compile errors: a SIX-argument `_engine.OnBar` call (Task 5 made it
  seven), a second declaration of `_owningEngine` (CS0102 — Task 7 owns it), and a full rewrite of
  the shell refusal plumbing Task 7 owns, minting a third spelling of the rejection reason. Amended
  to fix all three and to state what the task genuinely still owns: the §4.1 arbitration.
- **Task 28** deployed only four files and omitted `BreakBoxExits.cs`, which Phase 1 Task 10
  modified (it gained `StopSourceWarm`, now called by the strategy). Deploying the new strategy
  against the old Exits file is a CS0117 at F5 — **in a folder `check.sh` never inspects**, so the
  repo would look green while the chart would not compile. Amended to copy every `.cs` in
  `ninjascript/` and `cmp`-verify each, plus a basename-collision check between Indicators/ and
  Strategies/.
*Cost if wrong: a deploy that reports success and leaves NT8 unable to compile — precisely the
failure mode the repo's own gate cannot see.*
Tasks 24, 25: complete pending review (commits fff259a..2276290, 284 asserts, check.sh green)

**Ruling: the cloud gate ladder is 12 rungs, not the 10 I published earlier.** Task 20's Produces
block declared a 10-entry table as a forward declaration, before Tasks 24/25 existed. Those two
briefs' tests AND code independently agree on 12, adding "in trade" and "auto-trade" as their own
rungs instead of folding them. The implementer followed the briefs' hardcoded test assertions as the
acceptance criteria and updated `BbCloud.GateLadder` to match, flagging the contradiction rather than
silently picking one. Correct call: the 12-rung version is strictly more informative, and the tests
pin it.
CONTRACT UPDATED — cloud ladder (BreakBoxCloud.cs:87):
  0 warmup · 1 regime · 2 token · 3 in trade · 4 auto-trade · 5 pullback age
  6 cooldown · 7 reclaim · 8 direction · 9 close-in-range · 10 bar range · 11 leg
`task-65-brief.md` amended: the panel must DERIVE its rows from each engine's published
`GateLadder`, size `GateRows` as the max of the two, and never hard-code a copy.
*Cost if wrong: a panel that paints green rows while the engine is blocked, or mislabels the
blocker — a diagnostic that lies, in the component built to stop the strategy lying about why it
is idle.*

**Standing dispatch clause — stale line numbers.** Every Phase 3/4/5 brief was written against
`BreakBoxStrategy.cs` as it stood BEFORE Phase 1, which added ~120 lines to it, and Phase 2 added
more. Five briefs edit that file by line number: **40** (amended), **48**, **69**, **83**, **85**.
Task 40's numbers were measured stale by ~120 lines — deleting by them would have removed the wrong
properties and still compiled, the worst available failure. Every remaining dispatch that touches
those briefs must carry: *"every line number in this brief is stale — locate each edit by the code
around it, never by the number."*
Task 24: complete (commit cb8e68b, review clean — spec ✅, quality approved, zero findings)
Task 25: complete (commit 2276290, review clean — spec ✅, quality approved, one Minor)
  minor (deferred): `a.Engine = BbEntryEngine.Cloud` is assigned twice (default init + step-6
  fill). Harmless redundancy.

The fixture-rewrite question is CLEARED, and the reviewer's verification is worth recording as the
standard. It did not stop at "the original fixture could not reach the gates" (11 slope pushes
needed, 1 supplied per OnBar — confirmed against the empirical 21/21 `warmup` failures). It also
hand-traced the COUNTERFACTUAL the implementer rejected: filling the ring with an arbitrary constant
would make `slope ≈ eT/lookback`, large and positive, letting the instantaneous regime agree with
the seeded latch **by coincidence** — the tests would pass while silently no longer proving the
"gates read RegimeLatched, never regimeNow" claim they exist for. The chosen flat-at-eT fill forces
`slope = 0` every bar, so the instantaneous regime is pinned at 0 and the latch is the only thing
that can be driving the gates. The fix is the MORE rigorous option, not the more permissive one.

**Ruling: Task 46 must publish `BbEngine.GateLadder`, and its depth-9 collision is split.** Found by
checking, ahead of dispatch, what Phase 4's panel actually has to derive from. Two defects, both
invisible until Phase 4 and both of which make the panel lie:
  1. The box engine set gate strings inline and published **no ladder array** — so the panel, which
     I already forbade from hard-coding a copy, would have had nothing to derive from. Cloud
     publishes `BbCloud.GateLadder`; box now must too, 11 rungs.
  2. `Gate.Set("arms", …, 9)` and `Gate.Set("cooldown", …, 9)` both wrote depth **9**. Two blockers
     at one index means the panel labels one with the other's name. Cooldown moved to 10.
An assert pinning `GateLadder.Length == 11` was added so a rung cannot be added on one side only.
*Cost if wrong: the panel shows an operator the wrong reason the strategy is idle — the precise
failure the panel exists to end.*
Tasks 26, 27, 28: complete pending review (commits 2276290..4d4f77e, 284 asserts, check.sh green,
  deployed to NT8 Custom/Strategies)

=== PHASE 2 COMPLETE: the cloud engine is live in NinjaTrader ===

The implementer found and fixed TWO bugs no brief asked for, both in the same family — dispatch code
that hard-coded "not Cloud means Box":
  1. `AgeWorkingEntry` never aged a Cloud-owned working entry. `BbCloud` carries no internal
     trigger-life clock (`TriggerArmedBars` is declared but never incremented), so a cloud stop
     order would have rested forever — becoming a random re-entry hours after its thesis died.
     The shell now ages it against `_cloudCfg.TriggerLife`.
  2. `OnExecutionUpdate` called `_engine.OnEntryFilled()` unconditionally, so a CLOUD fill spent the
     BOX engine's memory. Now routed by `_owningEngine`.

Controller verified both by reading the code, not the report. The one that mattered:
`CancelWorkingEntry(why, engineDisarmed: true)` on expiry reaches `_cloud.OnTriggerExpired()`, which
restores the token. **Task 23 is therefore real and not cosmetic** — an expired cloud trigger no
longer burns the setup, which was the v1 behaviour.

  minor (deferred): `BbCloudState.TriggerArmedBars` is dead state — declared and reset in four
  places, never incremented or read, now that the shell owns the clock. Either wire it or delete it.
  It is also a field I published in the shared interface contract, so the final review should decide.
  minor (deferred): `a.Engine = BbEntryEngine.Cloud` assigned twice in `BbCloud.OnBar`.
  minor (deferred): `TriggerLifeSec` kept `Order = 7` rather than renumbering to 24; cosmetic
  ordering on the NT8 property grid.

**Ruling: the missing Cloud panel toggle lands in Task 67.** Phase 2 wired `EnableCloud` as a
`NinjaScriptProperty` only — seeded at DataLoaded, not togglable at runtime — and its implementer
correctly flagged that as unscoped. Task 67 owns CONTROLS and its brief is amended to build both
engine toggles through the same `Rebuild()` path it is already fixing (B5), never a direct
`BuildConfigs()` on the WPF thread.
Task 26: complete (commit 0f164a3, review clean — spec ✅, quality approved)
Task 27: complete (commit 4d4f77e, review clean — spec ✅, quality approved)
Task 28: complete (deploy only, no commit — review clean, spec ✅, quality approved)

=== PHASE 2 CLOSED: 9/9 tasks implemented AND reviewed clean ===
The reviewer ran the deploy verification itself rather than trusting the report: all SIX
`ninjascript/*.cs` files byte-for-byte identical to the deployed copies in
`Custom/Strategies/`, no basename collision with `Custom/Indicators/`, and the deploy wrote nothing
back into the repo. It also traced `OnTriggerExpired() -> RestoreToken() -> _st.Armed = true` to
confirm the restore is genuine rather than a call that goes nowhere.

Scaling contract verified independently: `grep -nE 'public int (Ribbon|Trend|Regime|Pullback|
MinPullback|MinBarsBetween)[A-Za-z]*Bars'` returns **zero hits**. No bar-valued dial survives on the
cloud parameter surface, and every conversion happens inside `BuildConfigs()` — which is what makes
a panel toggle rebuild a correctly-scaled config instead of a raw one.

  minor (deferred): `_cloudCfg.AllowLong`/`AllowShort` are wired in `BuildConfigs()` but
  `BbCloud.OnBar` never reads them — inert but consistent with the box config's pattern. Decide at
  the final review whether the engine should honour them or the wiring should go.

**Ruling: Task 49 is reduced to a verification task.** Checked ahead of dispatch and found Phase 2's
arbitration already wired the box arm of both routers (`BreakBoxStrategy.cs:735`, `:750`, `:908`),
so its stated deliverable is done. Its brief also named a router `OnTriggerExpiredFor(BbEntryEngine)`
that exists nowhere — expiry dispatches inline inside `CancelWorkingEntry`, which Task 7 owns.
Amended: do not create the phantom method, verify the three call sites still reach the box engine
after Task 40's rewrite, and **add the end-to-end assert that was impossible until now** — that
`_owningEngine` genuinely partitions the engines, so a Box disarm cannot cancel a Cloud entry or
vice versa. Nothing has tested that across two v2 engines, because until this phase the box engine
was still v1.
*Cost if wrong: Task 49 does nothing and the partition property stays untested — the arbitration
would be assumed rather than demonstrated.*

**Ruling: spec §14's fidelity-test correction was unassigned; it lands in Task 60.** The plan's own
file-structure table gives `BracketTests.cs — one case corrected` to Phase 4, but no Phase 4 brief
actually contains it. `FidelityImage2Splits` still asserts "observed 7 of 10" — a measurement spec
§2.1 refutes (the trade was 13 contracts split 7/6; only qty 7 at 1.25 pts reproduces the reference
HUD's exact $17.50). The test was pinning a refuted observation while being described as the
untouchable tripwire. `task-60-brief.md` amended with the exact replacement and an explicit
instruction that the OTHER two fidelity cases stay untouched.
*Cost if wrong: the suite would keep asserting a number the project's own forensics corrected — a
test that lies in the one file whose whole purpose is not lying about the reference geometry.*

**Ruling: spec §13's count-only instrumentation was unbuilt by any task; assigned to Task 63.**
Audited the remaining briefs against the spec and found this one missing. It matters more than its
size suggests: §13 step 1 is the FIRST thing done with the finished strategy — run it with orders
disabled, count how often each gate blocks, then tune. Without counters there is nothing to count
and the fallback is guessing constants and reading P&L, which is fitting noise twice. Amended
`task-63-brief.md`: an `int[]` histogram over `GateDepth` per engine, sized and named from each
engine's own published `GateLadder` so the printed line cannot drift from the ladder; raw arms
counted separately from fills (the post-cap count is true by construction and says nothing); reset
at the session roll; and every rung prints even at zero, because an absent rung reads as
"not measured" rather than "never fired".
*Cost if wrong: the strategy ships complete and uncalibratable, and the documented next step cannot
be taken.*
Task 40: complete (commit 5e9e502, review clean — spec ✅, quality approved, one Minor)
Task 41: complete (commit 84df463, review clean — spec ✅, quality approved)
Task 42: complete (commit a77f6d6, review clean — spec ✅, quality approved)
Task 43: complete (commit 71a130a, review clean — spec ✅, quality approved)

The fixture question is CLEARED, and sharper than the implementer put it: the reviewer found the
`fires == 0` assertion is **trivially true in this batch** — no code path sets `a.Fire = true` until
arming exists — so raising `BoxMeanSamples` locally could not have weakened it. The only assertion
the override actually protects is `Gate.Block == "box warming"` at the end of the tape, which is
exactly what the test's own docstring says it verifies. Legitimate repair of a fixture that went
stale when the feature it exercises started working.

Deletion verified by the reviewer running the grep itself: zero live references to any deleted
symbol, and `BbEntryEngine.Retrace` correctly survives as the retained enum value.

  minor (deferred): `BreakBoxCore.cs`'s top-of-file rationale comment literally names the deleted
  symbols ("SlotOf, BbBoxSource, HtfMinutes…"), so the brief's own `! grep -rnE …` verification can
  never report clean even though the substance is correct. A gate that cannot pass is a gate nobody
  will trust. Reword the comment (e.g. "the old box-source enum") so the prescribed check works.
  minor (deferred): `BreakBufferTicks` and `RequireCloseOutside` are live `NinjaScriptProperty`
  dials with zero readers — Tasks 40–43 left them for Task 46 to wire. If Task 46 does not wire
  them, they must be deleted: a dial on the parameter surface that changes nothing is a lie to the
  operator.
Task 44: complete (commit bfd936b, review clean — spec ✅, quality approved, zero findings)
Task 45: complete (commit 04bad2a, review clean — spec ✅, quality approved, zero findings)
Task 46: complete (commit 12a88a6, review clean — spec ✅, quality approved, zero findings)
Task 47: complete (commits 6eb371a, b017027, review clean — spec ✅, quality approved)

The disputed test-number change is CLEARED. The reviewer traced it bar-by-bar independently:
`AgeTrigger` uses `>`, so `TriggerLife = 2` needs three bars to expire — under the brief's two-bar
loop `T.Check(!st.Armed, …)` fails outright, so the test could not reach the behaviour it names. And
at exactly three bars the shared fixture's `BoxArmCooldown = 3` clears in the SAME `OnBar` call, so
the engine re-armed on the spot and burned the deliberate-refusal scenario before the test reached
it. Every original `T.Check`/`T.CheckInt` call and message is byte-identical; only two setup values
moved. Legitimate repair.

The four arming paths were verified genuinely distinct, which is the property this phase exists for:
`Arm()` increments · `OnTriggerExpired()` leaves the count alone (the market declined a live
attempt — spent stays spent) · `OnEntryRejected()` decrements (our refusal, so refund) ·
`TrackInside()` zeroes both counters on a close back inside the sealed box. v1 collapsed all four
into one boolean latch, which is why a trigger that never filled burned the box.

Also confirmed: the validity ratio computes `SealedMean()` BEFORE pushing this box's own range into
the ring — no self-reference biasing the ratio toward 1 — and no ATR term survives anywhere in the
validity path.

  minor (deferred): `SeedSealedRange` duplicates four lines of ring-buffer push logic that also live
  inline in `Seal()`. Two copies of the same push; worth a shared helper if anything touches it.
Tasks 48, 49: complete pending review (commits b017027..46e08ea, 316 asserts, check.sh green)

=== PHASE 3 COMPLETE: the box engine is rebuilt at the right scale ===
The two inert dials (`BreakBufferTicks`, `RequireCloseOutside`) were deleted rather than left on the
surface — a dial that changes nothing is a lie to the operator.

Honest limitation recorded by the implementer rather than overclaimed: `tests/ArbitrationTests.cs`
proves `BbEngine` and `BbCloud` share **no mutable state** — the necessary condition for the shell's
`_owningEngine` dispatch to be safe — but it cannot exercise the shell's actual if/else, which does
not run outside NT8. `check.sh`'s "compiles clean" is the only executable check on that half. That
gap is real and belongs in the final review's triage.
Task 48: complete (commit c1c4087, review clean — spec ✅, quality approved, zero findings)
Task 49: complete (commit 46e08ea, review clean — spec ✅, quality approved, zero findings)

=== PHASE 3 CLOSED: 10/10 tasks implemented AND reviewed clean ===

**The §8 auto-scaling contract is verified, with the right distinction drawn.** The reviewer grepped
all ~50 `NinjaScriptProperty` entries: every model HORIZON is in seconds and converted through
`BbScale.Bars` inside `BuildConfigs()` (13 conversion sites). The only bar-valued dials left are
`AtrPeriod`, `MaPeriod`, `E50Period`, `SwingStrength` — indicator **periods**, fed straight into
`WilderAtr`/`Ema`/`SwingDetector`, which is the period-equals-bars convention every platform uses
and is not what §8 is about. No bar-valued horizon survives.

It also traced the `RequireCloseOutside` deletion properly rather than accepting "it was dead":
Task 40 dropped the config field and flagged it openly; Task 46 then made the deliberate call to
hard-code close-required with the reasoning inline ("a wick through the edge is the single most
common way a box-breakout backtest lies to you; v1 made that a dial, it is not one"); and the
binding v2 spec never mentions it. Cleanup of surface already made dead by a documented design
decision — not a behaviour change wearing a cleanup's clothes.

**FOR THE FINISH — doc hygiene, found by review:** `docs/design.md` at the repo root is the v1
document. It still describes `RequireCloseOutside` and an active Retrace engine, neither of which
exists any more, and it describes a strategy that took zero trades as though it were the design.
It is the repo's front door. It must be replaced or clearly superseded before this branch is
presented as finished.

=== PHASE 4 — history and panel ===
Tasks 60, 61, 62, 63: complete pending review (commits 46e08ea..a0bf6ec, 357 asserts, check.sh green)
Both controller amendments landed: the fidelity-case correction (86bf5e8) and the §13 count-only
instrumentation (a0bf6ec). The strategy is now calibratable.

**Ruling: the unbounded in-memory history list is capped in Task 68.** Its implementer flagged that
`AppendHistory` grows `_history` even when the write guard refuses the file write — correct for the
panel (a Replay session should still draw its own curve) but unbounded, so a 2,000-iteration
optimisation sweep grows it without limit. It refused to invent a cap and flagged it instead.
Assigned to Task 68, which owns the chart that consumes the list, with three constraints: drop from
the FRONT so recent trades survive; never let the cache trim the FILE, which is the durable record;
and if a view needs more than the cap holds, **say so** rather than drawing a shorter curve that
looks like a quieter month — a chart that under-reports silently defeats the whole feature.
Task 60: complete (commits 320c253, 86bf5e8, review clean — spec ✅, quality approved)
Task 61: complete (commit 120a9dd, review clean — spec ✅, quality approved)
Task 62: complete (commit 35e246d, review clean — spec ✅, quality approved)
Task 63: complete (commits 8608e78, a0bf6ec, review clean — spec ✅, quality approved, one Important)

The write guard was verified UNBYPASSABLE, not just present: the reviewer grepped the whole tree for
`File.`/`StreamWriter`/`FileStream` and found exactly one write site, directly gated, with exactly
one caller. That is the check that protects the user's actual deliverable from a 2,000-iteration
sweep.

**Ruling: the §13 counters are structurally blind, and it must be fixed before Javier runs the
protocol. This is the most valuable finding of the whole execution.**

`auto-trade` sits at rung 4 of BOTH ladders and is a hard `return a;` short-circuit — pre-existing
Phase 2/3 design, untouched by this batch. But §13's calibration run is `_uiAutoTrade = false` for
the whole session (that is what "orders disabled" means, and the only way no position ever opens).
So **every bar stops at rung 4**, and rungs 5–11 for Cloud (pullback-age, cooldown, reclaim,
direction, close-in-range, bar-range, leg) and 5–10 for Box (window, budget, armed, break, arms,
cooldown) print `=0` for the entire run — not because those gates never block, but because they are
**unreachable while auto-trade is off**. Those rungs are precisely the G dials §13 step 3 names as
the tuning targets.

The printed `=0` is indistinguishable from "this gate never fires" — the exact failure mode the
amendment's requirement 3 was written to prevent, arriving by a route the amendment did not
anticipate. Javier would burn 5–10 Replay sessions and read a wall of zeros on the only numbers he
wanted.

**Decision: `canTrade` becomes a FINAL VETO, not an early gate.** Both engines evaluate the full
chain and record the deepest rung reached, then suppress firing. This preserves §5.2 step 1b's
intent exactly — nothing arms, nothing fires — while making the counters report the real
distribution. It also aligns with §9.3, which already says shell-level blocks (`AUTO-TRADE OFF`)
**replace the headline** rather than occupying a ladder rung. Queued as its own dispatch; it touches
both engines, so it does not run concurrently with the panel work.
*Cost if wrong: the calibration protocol reports zeros for every dial it exists to measure, and the
first documented step after this project is worthless.*

  minor→important (deferred to Task 68): the config digest's 16-key list omits decision-relevant
  dials — `AtrPeriod`, `SwingStrength`, the cloud ribbon periods, the entry window times,
  `MinBarsBetweenSec`. Restarting with a different `SwingStrength` would NOT get a new bucket, so
  genuinely different configurations merge onto one equity curve. That is the OPPOSITE failure from
  the one §10 excluded `risk` to prevent, and it defeats the same question: "is what I am running
  now working?"
  minor (deferred): the gate-summary `Print()` is not gated to realtime/replay, so it would flood
  the Output window inside a large optimisation sweep.
Tasks 64, 65, 66: complete pending review (commits a0bf6ec..cbb9de2, 376 asserts, check.sh green)

**Controller error, corrected by the implementer:** my Task 65 amendment said the box ladder is
"10 rungs"; my own Task 46 amendment specified 11, and the code and its assert both have 11. My two
amendments contradicted each other. No code impact — the panel sizes `GateRows` as
`Math.Max(BbCloud.GateLadder.Length, BbEngine.GateLadder.Length)` and derives labels by reference
rather than transcription, exactly as required, so it self-corrected. Recording it because a
controller amendment that drifts from the code is the same class of defect I have been catching in
the briefs.

Task 64 exposed a real gap the briefs did not cover: removing the v1 HUD fields broke
`#region Readouts` (`UpdatePanelStatus` / `UpdateHud`), which is not in Task 64's file list but read
them. Left alone, the file would not compile between Task 64 landing and Task 69's rewrite. The
implementer patched both to a minimal interim version so every commit compiles standalone, flagged
it as code beyond the briefs, and named Task 69 as the real replacement. Correct call.
Task 64: complete (commit 7bb3fc9, review clean — spec ✅, quality approved, zero findings)
Task 65: complete (commit 411e92d, review clean — spec ✅, quality approved, zero findings)
Task 66: complete (commit cbb9de2, review clean — spec ✅, quality approved, zero findings)

The ladder is genuinely DERIVED, verified live: `BoxGates`/`CloudGates` are direct references to
`BbEngine.GateLadder` (11 rungs) and `BbCloud.GateLadder` (12), not transcribed copies, and
`GateRows = Math.Max(...)` is computed at build time. It cannot drift from the engines by
construction — which is the property that stops the panel painting green while the engine is
blocked.

The Task 66 test repair is CONFIRMED legitimate by hand-trace: the brief's original
`Newest(1) == "12:55"` only holds if the duplicate WAS pushed — the test asserted the very bug
de-duplication exists to prevent. The implementer fixed the assertion, not the ring, and the ring
already matched its own doc comments.

canTrade final-veto fix: complete pending review (commit acce33c, 400 asserts, check.sh green)
The implementer proved the fix rather than asserting it: it stashed the two engine files, reran the
new asserts against the PRE-FIX code, got exactly the four expected failures, then restored and
reran clean. Red before, green after — the counters were genuinely blind and now are not.

**FOR JAVIER, not for this branch:** `scripts/nt8c-hook.sh` in the parent workspace has no BreakBox
carve-out for its multi-file `ninjascript/` layout (SizeMap has one). Every edit to a pure engine
file fires a false-positive CS0246/CS0103 block from the hook's per-file compile, because it cannot
see sibling files. `scripts/check.sh` — this project's real gate — documents that exact limitation
and is green. Editing a hook in the parent repo is a config change outside this branch, so I am
surfacing it rather than making it.
canTrade final-veto fix: complete (commit acce33c, review clean — spec ✅, quality approved, zero
findings)

The check that mattered was done properly: the reviewer traced every line that now executes between
the old checkpoint and the new one, in BOTH engines, and confirmed they are all pure reads whose
only side effect is `_st.Gate.Set(...)` — a diagnostic write. Nothing touches `Armed`, `Ext`,
`AgeBars`, `BarsSinceLastArm`, `ArmsUp`/`ArmsDn`, `TradesThisBox`/`TradesToday` or `LastArmBar`.
**Zero new state mutations under `canTrade == false`.** Arming and firing still sit strictly after
the veto. Trading behaviour is identical; only the reported blocker changed.

Both ladders confirmed untouched (11 and 12 rungs, `"auto-trade"` still index 4), and the two
deep-rung asserts were confirmed structurally impossible to pass against the pre-fix code, which is
what makes them proof rather than decoration.
Tasks 67, 68, 69: complete pending review (commits acce33c..ec0b836, 417 asserts, check.sh green)

=== PHASE 4 COMPLETE: the vertical panel and the user's history chart are built ===

**Controller error #2, correctly resolved by the implementer:** my Task 67 amendment says the engine
toggles "must NOT go through `TriggerCustomEvent`" and then, one sentence later, to "route them
through the same `Rebuild()` path" — which itself uses `TriggerCustomEvent`. Internally
contradictory. It followed the brief's concrete code block, which was the right tie-breaker, and
left the resolution in a comment.

**Ruling: the config digest's key list is a PRINCIPLE, not the five dials I happened to name.** The
implementer read my list as closed and extended the digest to exactly those five — reasonable
reading of what I wrote, but wrong about what I meant, and I said so ambiguously. `TrendLineSec`,
`PullbackMaxSec` and every other dial that changes what is traded must be in the digest too.
A digest that misses one merges genuinely different configurations onto a single equity curve, which
is precisely the failure it exists to prevent and defeats the question the chart was built to
answer: *is the configuration I am running now working?* Assigned to the final batch.
*Cost if wrong: the user reads one curve as evidence about a config it does not describe — the
feature actively misleads instead of merely under-delivering.*

  minor (accepted): `BbHistory.MaxInMemory = 2000`, the implementer's judgment call — ~3.3x the ~600
  trades a 20-day view needs at the default daily cap. Sound sizing; the amendment gave a principle
  and no number.
Task 67: complete (commit 56de20e, review clean — spec ✅, quality approved, zero findings)
Task 68: complete (commit 4a7f0af, review — spec ❌ on digest completeness ONLY, quality Important;
  the fix is dispatched, see below)
Task 69: complete (commit ec0b836, review clean — spec ✅, quality approved, zero findings)

Task 69 verified at the level that matters: **exactly ONE dispatcher hop per bar**, the interim
`UpdateHud` region REPLACED (grep returns zero hits, not appended-to), and the ladder's out-of-range
case genuinely handled — the box's row 11 renders blank rather than borrowing the cloud's label,
which is reachable because the ladders are 11 and 12 rungs.

Task 68's chart honesty verified in detail: the cap drops from the front; the disk write uses the
record computed BEFORE the trim block, so the render cache can never trim the file; and the
"capped" notice cannot false-negative.

**The digest gap is far larger than estimated.** The reviewer enumerated all 58
`NinjaScriptProperty` dials and found **35 decision-changing dials untracked against 23 tracked** —
the implementer had self-flagged 3. Two different strategy instances started with different
`BoxLookbackSec`, or a different `ManualStopTicks` while the stop source is `Man`, get the SAME
digest today and render as one un-dimmed curve. That is the contamination the seam exists to
prevent, across most of the parameter surface. The full 35-dial list was sent to the implementer
already working on the fix, so it does not re-derive it.

=== PHASE 5 — Vision indicator ===
Digest fix + Tasks 81, 82, 83, 84: complete pending review (commits ec0b836..7d30273, 417 asserts,
check.sh green)

**Ruling: both of the implementer's digest judgment calls are accepted.**
  - `BaseQuantity` EXCLUDED alongside the risk multiplier. Correct: `qty = BaseQuantity *
    _uiRiskMult`, so both scale size and neither changes a decision. Excluding one and tracking the
    other would fragment the curve on half of the same formula.
  - `DailyLossLimit` / `DailyProfitTarget` KEPT IN, explicitly not treated as risk dials. Correct,
    and the sharper call of the two: they can HALT the session, so they change **which trades
    happen**, not merely their size. A session that stopped at noon is not the same experiment as
    one that ran to the close.

**CRITICAL — cold-start ordering defect, found in the last batch, verified by the controller.**

`State.DataLoaded` calls `BuildConfigs()` at `BreakBoxStrategy.cs:320`. `BuildConfigs()` reads the
UI-mirror fields — `_uiBreakOn` / `_uiLongOn` / `_uiShortOn` at `:364-366`, `_cloudCfg.AllowLong` /
`AllowShort` at `:436-437`. Those mirrors are only synced from their properties at `:336-341`,
**afterwards**, so at that moment they hold C#'s `false` default. Nothing re-runs `BuildConfigs()`
until a panel toggle.

Net effect on a fresh chart with no panel interaction: `_cfg.EnableBreak = false`,
`_cfg.AllowLong = false`, `_cfg.AllowShort = false` — **the box engine starts disabled and
directionless.** The cloud engine survives, narrowly: `BbCloud.OnBar` does not read
`AllowLong`/`AllowShort` (that wiring was already found inert), and the cloud path is gated at bar
time by `_uiCloudOn`, which IS synced by then.

**This is the v1 failure signature reintroduced through a different door, in the final task:** a
strategy that silently does nothing and tells the operator nothing. Shipping it would mean Javier
opens a chart, sees no trades, and has no reason — which is precisely the experience this entire
rebuild exists to end.

Found by the implementer of the digest fix while working inside the same method. It refused to fix
it out of scope and escalated instead — the right call, and the second time in this execution that
refusing to act speculatively caught something material.

**Ruling: fix before anything ships.** The correction is to move the mirror sync ABOVE the
`BuildConfigs()` call — `_barSec` must still be measured first, since `BuildConfigs()` divides by
it. Queued as its own dispatch so it does not collide with the Vision batch in flight.
Digest fix: complete (commit bdd121e, review clean — spec ✅, quality approved)
Task 81: complete (commit 8a3d9b3, review clean — spec ✅, quality approved)
Task 82: complete (commit 7bf64d0, review clean — spec ✅, quality approved)
Task 83: complete (commit d9b1fa5, review clean — spec ✅, quality approved)
Task 84: complete (commit 7d30273, review clean — spec ✅, quality approved)

The reviewer hand-enumerated all **62** `NinjaScriptProperty` dials: 58 tracked by name, 2 excluded
by rule (`risk`, `qty`), 4 correctly never listed (pure visual toggles). 62/62 accounted for, no
asymmetric pairs. It also verified `Canonical` compares the key with an ordinal `string.Equals` on
the part before `=`, so there is no substring collision between e.g. `trig` and `trigoff`.

**A connection worth recording:** the engine/direction toggles are tracked INDIRECTLY, via their
`_ui*` mirrors rather than the raw properties — which the reviewer rightly calls *more* correct,
since the live toggle state is what changes decisions. But that means **the cold-start ordering
defect corrupts the digest too**: on a fresh chart the first `BuildConfigs()` hashes
`cloud=?/box=0/long=0/short=0` from unsynced mirrors. The queued ordering fix therefore repairs both
the dead box engine and the wrong first digest. One fix, two failures.

Test-coverage gap assessed and accepted: there is no reachable runner-executed test asserting "every
NinjaScriptProperty is in the digest", because the enumeration lives in the NT8-dependent shell.
The alternatives are a hand-duplicated dial list inside the test project — the second copy that
drifts and defeats its own purpose — or reflection against an assembly the harness does not build.
Structural verification is the right instrument here, and it was done.
Tasks 85, 86, 87: complete pending review (commits 7d30273..9b8957e, 417 asserts, check.sh green,
  all 8 files deployed and cmp-verified)

Two things this batch caught that would have shipped silently:
  - **`BreakBoxHistory.cs` was missing entirely from the deployed tree.** Every earlier deploy had
    omitted it. Only a full-tree deploy with per-file `cmp` surfaces that class of gap.
  - **Task 86's brief globbed the history file by `Instrument.MasterInstrument.Name` ("MNQ")**, but
    the strategy writes it keyed off `Instrument.FullName` ("MNQ 12-25", spaces to underscores).
    The brief's pattern matches zero files the strategy ever writes — Vision would have loaded
    **zero markers on every real chart**, silently, and looked merely uneventful rather than broken.
    Fixed to derive the glob from `FullName`.

Task 87 steps 3–5 (open NT8, F5, add the indicator, compare against the four reference frames,
record disagreements) are unexecuted and unexecutable here — they need a human at a live GUI. That
is correctly reported rather than claimed.
Cold-start ordering fix: complete (commit 8105345, review clean — spec ✅, quality approved)

Verified independently by the reviewer: exactly one block moved (6 assignments + comment), nothing
else reordered, `_barSec` still first, and all six property-backed mirrors synced before
`BuildConfigs()` runs. `_uiAutoTrade` confirmed NOT a property mirror — no backing
`NinjaScriptProperty` exists, its only writer is the panel's toggle handler, and `BuildConfigs()`
never reads it. The digest is fixed as a side effect, since it is built from the same six mirrors.

**The "no reachable test" claim was itself verified rather than accepted** — the reviewer checked
the csproj's compile set, confirmed both the sync block and `BuildConfigs()` read NT8-only members
that cannot exist outside a `Strategy`-derived class, confirmed `check.sh`'s NT8 half is
compile-only with no runtime instantiation, grepped for any Mock/Harness/TestStrategy pattern in the
repo (none exists), and read the one test file whose name looked promising. It could not find a seam
either. That is the right way to close an "I could not test this": verify the claim, do not take it.

  minor (deferred): the load-bearing comment names three of the six affected mirrors in its
  consequence prose, though it generalises correctly at the end.
Task 85: **Critical finding — fix dispatched** (commit f6a0be4, spec ✅ but quality Critical)
Task 86: complete (commit 9b8957e, review clean — spec ✅, quality approved, zero findings)
Task 87: complete (deploy verified independently by the reviewer — all 8 files byte-identical, no
  basename collision; steps 3–5 correctly reported as needing a human at a live GUI, no completion
  claimed)

**CRITICAL: the accumulation box renders with ZERO WIDTH.** `PaintBox()` draws
`Draw.Rectangle(..., box.SealedAt, box.Low, Time[0], box.High, ...)` — but `Seal()` runs inside the
SAME `OnBar()` the strategy is processing, so `box.SealedAt` **is** `Time[0]` at draw time. Both
anchors are the same timestamp, and a `box.Id == _lastDrawnBoxId` guard means it draws exactly once
and never widens. The rectangle collapses to a hairline.

Root cause is in the brief I dispatched — it specified `Time[0]` verbatim, so the implementer
matched it exactly. **No test could catch it**: nothing in the suite touches `Draw.Rectangle`.

Why it matters beyond cosmetics: the reference shows a ~7-bar box and the spec ties
`BoxLookbackSec = 210` to that measurement. Javier's visual checklist row 4 is literally "white
rectangles: size ~7 bars" — he would have seen nothing and reasonably concluded the box engine was
cold-starting or failing validity, chasing the wrong component entirely. In the one tool whose
entire job is looking right.

The reviewer found it by going past the literal check I asked for (I asked whether the box's
VERTICAL extent came from sealed edges rather than live bars — it does) and tracing the HORIZONTAL
extent on its own initiative.

Fix dispatched, with the further question of whether the LEFT edge is also wrong: a box formed over
a lookback window that seals at `SealedAt` arguably began `BoxLookback` bars earlier, and the
reference draws its rectangle AROUND the consolidation rather than starting after it.

  minor (fixed in the same dispatch): `LoadHistory()` catches `IOException`, but
  `UnauthorizedAccessException` does not derive from it — a permissions-denied file would bubble out
  uncaught during `State.DataLoaded` and take the indicator down.
Box-geometry fix: ADDRESSED (commit dddf86e) — the Critical zero-width defect is genuinely gone.
The implementer chose the better geometry than my dispatch suggested: it rejected copying the
strategy's `Time[0].AddHours(4)` convention because projecting the box FORWARD presents it as a
still-relevant zone, whereas Vision's box edges are the range of the last `BoxLookback` CLOSED bars
— so it extended BACKWARD by that same lookback, drawing the rectangle around the actual
accumulation, with no magic number. It also used bar indexing rather than subtracting seconds, so
the left edge survives session gaps and weekends.

**Residual found by the scoped re-review, and being fixed:** the drawn rectangle is one bar too
wide, on the wrong side. `WindowRange()` reads its ring BEFORE pushing the current bar, so the
measured window is `Time[1]..Time[BoxLookback]` — it excludes the sealing bar by design. But
`SealedAt` IS `Time[0]`, so the rectangle spans `Time[0]..Time[BoxLookback]` = `BoxLookback + 1`
bars, and its right column is a bar **whose high and low were never part of the range** (`Form()`
gates only that bar's CLOSE). On a real chart, a sealing bar whose wick exceeds the box would be
drawn **poking outside the box that supposedly contains it** — in the one tool whose entire job is
looking correct. Right edge moved to `Time[1]`.

**The re-reviewer also correctly pushed back on the "untestable" claim.** The `Draw.Rectangle` call
is genuinely unreachable from the pure suite, but the ARITHMETIC the geometry rests on is not — and
the neighbouring fact was already pinned at `tests/BoxTests.cs:170` ("the current bar does not widen
its own window"). A test connecting *the bar a box seals on* to *the bar-index range its High/Low
were measured over* would have caught this off-by-one mechanically. That test is being added.
Box off-by-one fix: complete (commit 1944b80, 421 asserts, check.sh green both halves, redeployed)
The 4 new asserts pin the fact the bug lived between — that a box's High/Low genuinely exclude the
sealing bar's own extremes — which is the pure-engine arithmetic the drawn geometry rests on.

**Controller's own final verification, run independently:**
  - `scripts/check.sh` green on BOTH halves: 421 asserts, all NT8 files compile clean as one unit.
  - Deploy verified file by file. Seven files byte-identical. `BreakBoxVision.cs` showed a
    difference that turned out to be **positive evidence**: NinjaTrader had rewritten it with CRLF
    and appended its own `#region NinjaScript generated code` — the wrapper methods NT8 emits when
    it successfully compiles an indicator. Content is identical up to that region.
    **So Vision has already compiled inside NinjaTrader itself, not merely against its references.**
    `BreakBoxStrategy.cs` has no such region, which is expected — NT8 generates those wrappers for
    indicators, not strategies.
  - Note for any future deploy check: `cmp` is the wrong test for a deployed INDICATOR once NT8 has
    compiled it. Compare the repo file against the deployed file's prefix with line endings
    normalised.

=== FINAL REVIEW — triage pass ===

**RULING REVERSED, and the reversal is correct.** At the Phase 2 close I filed
`_cloudCfg.AllowLong`/`AllowShort` being unread as "inert but consistent with the box config's
pattern" and deferred it as a minor. **That was wrong on the facts.** The box engine DOES read its
equivalents (`BreakBoxCore.cs:359-360`); the cloud engine does not. They are inconsistent, not
consistent — and the panel exposes a SINGLE pair of Buy/Sell toggles feeding both.

Operator consequence: turn **Sell** off, watch short *box* entries correctly stop, reasonably
conclude the toggle is strategy-wide — and keep receiving short entries from the **cloud** engine,
which is the primary one, with nothing on the panel saying otherwise. **An order in a direction the
operator explicitly disabled.** Zero test coverage existed to catch it.

I should have escalated this when the Phase 2 review first surfaced it instead of accepting the
"consistent" framing. Fix dispatched.

Triage of the other 14 deferred minors, verified still-present against the finished source:

FIX EVENTUALLY (none blocking):
  - `BbCloudState.TriggerArmedBars` — dead state in a published contract: declared, reset in four
    places, never incremented or read since the shell took the clock. A trap for whoever next
    touches Vision or the cloud engine.
  - `KillToken("closed through E50")` unreachable — `UpdateRegime` clears on the identical predicate
    one step earlier. Confirmed it does NOT corrupt the calibration counters (the correct rung is
    still recorded); only that detail string can never surface.
  - `SeedSealedRange()` duplicates `Seal()`'s four-line ring push.
  - The gate-summary `Print()` is not State-gated — would flood the Output window inside a large
    optimisation sweep, though not during the owner's actual next step.

FINE TO LEAVE: the rest are stale planning-doc text, cosmetic property ordering, unexercised
defensive guards, or historical rationale in comments. One is already resolved
(`BreakBufferTicks`/`RequireCloseOutside`, deleted in Phase 3).

Notably, the reviewer checked whether `BreakBoxCore.cs`'s header comment naming deleted symbols
breaks a live gate — it does not. That grep was a one-time Task-40 acceptance check, not part of
`check.sh`. Correctly downgraded from my earlier note.

**All four late fixes re-verified as correctly diagnosed and closed against the finished code:** the
`canTrade` final veto, the digest completeness, the cold-start ordering, and the box off-by-one.

=== FINAL REVIEW — claims + dead-code pass ===

**CLAIM 1 (no bar-valued horizon on the parameter surface): TRUE, no exceptions.** All 59 properties
enumerated in both the strategy and Vision. Every `*Sec` horizon converts through `BbScale.Bars`
inside `BuildConfigs()`. Everything exempt is legitimately exempt — indicator periods, confirmation
counts, ratios, tick offsets, dollar amounts, and wall-clock HHMM marks (which are compared against
`Time[0]` seconds-of-day and never fed to the converter).

**CLAIM 2 (the panel cannot lie): TRUE, with two exceptions.**
  1. **The ENGINE LOG section is permanently dead.** `EngineLog(string)` exists, its own comment
     claims it is "called from OnBarUpdate and the order handlers", and **it has no callers
     anywhere**. The ring stays empty forever, so that panel block always renders blank. Not a false
     statement — a UI section that looks live and is not. This is a real gap in what the user asked
     for.
  2. `"READY"` reflects only SHELL-level clearance (not locked out, not in trade, ATR warm,
     auto-trade on) and says nothing about the active engine's own gate chain — so the dot can read
     green while a gate blocks. Not the v1 disconnect (the ladder directly below names the true
     blocker), but the headline uses the terse gate NAME rather than the descriptive detail. Wording
     pass, not a correctness bug.

**CLAIM 3 (a failed attempt does not burn the setup): TRUE for the cloud, and the box is
INTENTIONALLY different — the DOCS are what is wrong.**
The box's `OnTriggerExpired()` deliberately does not refund, with its rationale inline (*"That IS an
attempt... refunding it here would let a sustained break outside the box re-arm forever"*) and
pinned by two tests. That is the design I recorded at Phase 3 and a reviewer verified then:
**expiry SPENDS (the market declined a live order), rejection REFUNDS (we refused it ourselves).**
The mechanics genuinely differ — a cloud token versus a per-edge arm budget — and §6.2's actual
promise IS met: v1's boolean latch needed a near-impossible clearing condition, while the budget is
bounded and clears on an ordinary inside close.

So the **code is right**; the spec (§5.2 step 7, §6.2, the B3 row) and `docs/design.md` overstate it
as "restores/refunds for both engines". Fix the prose, not the behaviour.

**Should-fix, all confirmed dead or overclaiming:**
  - `SeedSealedRange` — called only from tests, never from the shell, and `BbTradeRecord` has **no
    box-range field**, so there is nothing in the history file to seed from. The cold-start seam was
    never fully designed, not merely unwired. Both the spec (§6.1) and `design.md` claim day 1 is
    not a dead day; it is. The panel is honest at runtime ("box warming n/N") — the docs are not.
  - `CoveredQty` — dead, and its comment claims the shell compares it every bar as protection
    against a naked position. The real protection exists and works (`OrderState.Rejected` flattens
    immediately), just not via this. Delete it or wire it.
  - `ShowHud` — a `NinjaScriptProperty` with zero readers; its three siblings each gate a real draw
    call. Vestigial.
  - Doc drift: `design.md` says 417 asserts (now 421) and "58 dials" (59 keys, 2 excluded → 57).

**Controller-verified directly (the trace nothing else had covered): both engines enabled at once.**
  - Cloud is evaluated first; the box is evaluated only `if (!a.Fire)`, so on a bar where the cloud
    fired the box is never consulted — no double-fire.
  - `positioned = _inTrade || _entryPending`, and it is passed INTO both engines *and* re-tested at
    `if (a.Fire && !positioned) SubmitEntry(a)`. A live working entry from either engine therefore
    suppresses both engines and the submit. Double-protected, deliberately ("SubmitEntry while
    positioned is the one mistake that costs real money, and it is cheap to refuse twice").
  - `_owningEngine` is stamped inside `SubmitEntry` from `a.Engine`, and only one submit can occur
    per bar, so it cannot point at the wrong engine.
  - The calibration counters read each engine's own `Gate.GateDepth` directly, never a fill or a
    position — so they count correctly with submissions disabled, which is the mode the protocol
    runs in.

**The end-to-end trace reviewer never reported** — two idle cycles, two direct requests, no answer.
Recording that honestly rather than implying its pass happened. Coverage of its scope came from
elsewhere and is NOT complete:
  - Both engines enabled at once — **verified by the controller directly** (above). This was its
    highest-value item.
  - The order-event race — **verified by the controller directly** (flags before every submit,
    handlers gated on signal name).
  - The box's expiry/rejection paths — verified in detail by the claims reviewer, which read both
    callbacks and the two tests that pin them.
  - **NOT independently traced end to end: a full cloud signal from bar close through the bracket to
    the history record and panel render.** Its constituent parts were each reviewed when they
    landed, and `check.sh` proves it compiles as one unit, but no single pass followed one signal
    the whole way. That gap is real and belongs in the handover.

=== FINAL CLEANUP: all 7 findings ADDRESSED, re-review PASS, no new breakage ===
Commits 9562221, df631ff, 1e017ec, f2a1e1d. 435 asserts, check.sh green both halves, redeployed.

Verified by the re-reviewer, notably:
  - `EngineLog` is now called from `OnBarUpdate`, `CancelWorkingEntry`, `OnExecutionUpdate`,
    `Lockout()` and `PanelToggleLockout` — and the one caller reachable from a click handler goes
    through the `TriggerCustomEvent` bridge, so it never runs on the WPF thread. Two layers of
    de-duplication (per-engine last-logged-gate, plus the ring's own) and **no call site logs every
    bar**.
  - The box's new `"direction off"` rung was APPENDED at index 11, indices 0–10 untouched, with the
    pinning assert updated to 12 — and the panel sizes itself with `Math.Max` of both ladders, so
    there is no constant anywhere to drift.
  - **The expiry/refund CODE is byte-identical** — only the prose changed, which was the whole point.
  - The digest's canonical key list was counted by hand: 59 keys, 2 excluded, 57 hashed. Matches the
    corrected docs.

=== BRANCH COMPLETE ===
60 commits · 20 files · +5,921/−1,123 · 435 asserts · both gates green · deployed and verified.
All 47 planned tasks plus 5 unplanned fixes found by review: the canTrade final veto, the config
digest completeness, the cold-start config ordering, the box rectangle geometry (twice), and the
cloud's ignored direction gates.
