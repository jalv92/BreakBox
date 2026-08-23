Below, "T*n*" = Task *n*. Line numbers cited by the plan were re-verified against the current tree where relevant.

---

## A. Two definitions of the same type / member

**1. `BbScale` is defined twice, in two files, with contradictory semantics.**
T1 creates `public static class BbScale` in `BreakBoxTypes.cs` (namespace `BreakBoxCore`). T48 creates `public static class BbScale` again, in `BreakBoxCore.cs`, same namespace. Both files are in `tests/BreakBox.Tests.csproj` *and* in `check.sh`'s single compilation unit → `error CS0101: The namespace 'BreakBoxCore' already contains a definition for 'BbScale'`. T48's own note ("If Phase 2 already added an identical helper, delete this one") is not enough: it must be deleted unconditionally, and the correction is not cosmetic because the two disagree on the degenerate case (item 2).
**Fix:** delete the `BbScale` block from T48 Step 3 entirely; T48 keeps only the `BuildConfigs()` conversions and the property surface.

**2. `BbScale.Bars` has two incompatible contracts for `barSec < 1`.**
T1: `if (barSec < 1 || horizonSecs < 1) return min;` — asserted by `T.CheckInt(BbScale.Bars(600, 0, 2), 2, "a nonsense bar size floors rather than dividing by zero")`.
T48: `if (barSec < 1) barSec = 1; int n = secs / barSec;` — asserted by `T.CheckInt(BbScale.Bars(180, 0, 1), 180, "a zero bar size does not divide by zero")`.
Same call, 2 vs 180. With one class both asserts cannot pass.
**Fix:** keep T1's semantics; delete the assert `T.CheckInt(BbScale.Bars(180, 0, 1), 180, ...)` from T48's `SecondsScaleToBars` and replace it with `T.CheckInt(BbScale.Bars(180, 0, 2), 2, "a zero bar size floors")`.

**3. `_owningEngine` is declared three times.**
T7: `private BbEntryEngine _owningEngine;` (after `_pendingAction`, `:111`).
T27 Step 5: `private BbEntryEngine _owningEngine = BbEntryEngine.Cloud;` (after `_entryBarsWaiting`, `:116`) → `error CS0102: the type already contains a definition for '_owningEngine'`.
T49 assumes it already exists ("add next to `BuildConfigs` if Phase 2 has not already").
**Fix:** T7 owns the declaration; T27 Step 5 drops the field and only sets it. If the `= BbEntryEngine.Cloud` initialiser is wanted, put it in T7.

**4. `BbConfig.MaxTradesPerDay`: 30 (T9) vs 20 (T40).**
T9 sets `public int MaxTradesPerDay = 30;` and pins it with `T.CheckInt(cfg.MaxTradesPerDay, 30, "the daily cap sits above the design frequency")` in `ShellTests`. T40's rewritten `BbConfig` writes `public int MaxTradesPerDay = 20;`, silently reverting B9 and turning T9's assert red.
**Fix:** T40's `BbConfig` must read `public int MaxTradesPerDay = 30;` with T9's comment carried over.

**5. `BbConfig.TriggerLifeBars` (T3) vs `BbConfig.TriggerLife` (T40) — and a bare `TriggerLifeBars` in T27.**
T3 writes `_cfg.TriggerLifeBars = BbScale.Bars(TriggerLifeSec, _barSec, 1);` and `if (_entryBarsWaiting <= _cfg.TriggerLifeBars)`. T40 renames the field to `TriggerLife`; T48/T49 use `_cfg.TriggerLife`. T27's `AgeWorkingEntry` uses **the deleted NinjaScriptProperty**: `int life = _owningEngine == BbEntryEngine.Cloud ? _cloudCfg.TriggerLife : TriggerLifeBars;` — T3 replaced `TriggerLifeBars` with `TriggerLifeSec`, so this is `error CS0103` *and* it re-breaks T3's own conformance grep (`grep -n 'TriggerLifeBars' ... | grep -v '_cfg\.'` must print nothing).
**Fix:** pick `TriggerLife` as the field name from T3 onward (T3 writes `_cfg.TriggerLife = BbScale.Bars(TriggerLifeSec, _barSec, 1);`), and T27's line becomes `_owningEngine == BbEntryEngine.Cloud ? _cloudCfg.TriggerLife : _cfg.TriggerLife`.

**6. `TriggerLifeSec = 120;` is assigned twice in `SetDefaults`.**
T3 Step 3 ("Default at line 188") and the Phase 2 cloud block ("`TriggerLifeSec = 120;`" under `// ---- Cloud (§5.4)`).
**Fix:** delete the line from the Phase 2 cloud defaults block; T3 owns it.

---

## B. Consumed but never produced

**7. `BbEntryEngine.Cloud` does not exist when T5/T7 use it.**
Current `BreakBoxCore.cs:36-39` is `enum BbEntryEngine { Break = 0, Retrace = 1 }`. T7 writes `_owningEngine != BbEntryEngine.Cloud` in three places and T7's `PanelManualEntry` edit; nothing in Phase 1 adds `Cloud = 2`. The value is only introduced by T40's rewritten enum (Phase 3). `error CS0117: 'BbEntryEngine' does not contain a definition for 'Cloud'`.
**Fix:** add a Step to T7 (before Step 3) that edits `BreakBoxCore.cs:36-39` to the contract enum `{ Break = 0, Retrace = 1, Cloud = 2 }`, and list it in T7's Produces.

**8. `_uiBoxOn` is consumed by T63 and T67 and produced by no task.**
T63's Consumes says "from Phase 3's shell: `private bool _uiCloudOn, _uiBoxOn;`" and its `Canonical` list has `"box=" + (_uiBoxOn ? "1" : "0")`. T67's `BuildControlsSection` reads and writes `_uiBoxOn`. But T40 explicitly produces `private bool _uiBreakOn, _uiLongOn, _uiShortOn;` and Phase 2 sets `_uiBreakOn = EnableBreak;`. `error CS0103: '_uiBoxOn'`.
**Fix:** replace every `_uiBoxOn` in T63 and T67 with `_uiBreakOn` (button label "Box" is fine; the field is `_uiBreakOn`).

**9. `BbCloudState.SlopeBuf` is never sized on the strategy path.**
Contract: "`double[] SlopeBuf; // sized cfg.TrendSlopeLookback + 1 at DataLoaded`". T82 (Vision) does it: `_cloudSt.SlopeBuf = new double[_cloudCfg.TrendSlopeLookback + 1];`. Phase 2's shell only does `_cloudState = new BbCloudState();` in `State.Configure` and `_cloud = new BbCloud(_cloudCfg, _cloudState);` in `DataLoaded` — `SlopeBuf` stays null → `NullReferenceException` on the first `BbCloud.OnBar`.
**Fix:** in T26's `State.DataLoaded`, immediately before `_cloud = new BbCloud(...)`, add `_cloudState.SlopeBuf = new double[_cloudCfg.TrendSlopeLookback + 1];` (or make `BbCloud`'s constructor size it, and change T82 to stop doing it by hand).

**10. `BbBox.AnchorStart` is deleted by T40 and still used by T85.**
T40's Produces: `BbBox { High, Low, SealedAt, Valid, Id, Range }` — `AnchorStart`/`AnchorEnd` are gone (T40 even rewrites `DrawBox` at `:799` to use `box.SealedAt`). T85's `PaintBox` calls `Draw.Rectangle(this, "bbv_box_" + box.Id, false, box.AnchorStart, box.Low, Time[0], box.High, ...)` and its Consumes cites `BbBox { Id, High, Low, AnchorStart }` at the v1 line numbers.
**Fix:** T85 uses `box.SealedAt`; correct T85's Consumes to `BbBox { Id, High, Low, SealedAt }`.

**11. T85 assigns an `int` property into a `double` config field on a dial the two surfaces type differently.**
T48: `[Range(1.0, 99.0)] public double BoxRangePctile`; `BbConfig.BoxRangePctile` is `double`. T85: `[Range(1, 99)] public int BoxRangePctile` in Vision. It compiles, but the same dial has different resolution in the two places the plan asks the user to keep identical (T87 step 3: "set its dials to the strategy's by hand").
**Fix:** T85 declares `public double BoxRangePctile` with `[Range(1.0, 99.0)]` and default `35.0`.

---

## C. Ordering violations

**12. T27 consumes the six-argument `BbEngine.OnBar` that T5 already replaced.**
T27's Consumes is explicit: "`BbEngine.OnBar(bar, secs, sessionDate, atr, atrWarm, positioned)` — the CURRENT six-argument signature at `BreakBoxCore.cs:201`", and its arbitration block calls it that way. T5 (Phase 1) already made it seven arguments with `canTrade`, and the shared contract mandates seven. `error CS1501`.
**Fix:** T27's arbitration becomes `a = _engine.OnBar(bar, secs, sessionDate, _atr.Value, _atr.IsWarm, canTrade, positioned);` and its Consumes cites the 7-arg contract signature. Delete the "Note for the box-engine phase" paragraph.

**13. T27 declares the box engine has no `OnEntryRejected` — T6 added it two phases earlier.**
T27's router: `// The box engine gains its own OnEntryRejected in the box rewrite (§6.2). Until then a refused box entry still burns its edge — that is B3 unchanged, not a defect introduced here.` T6 produced `BbEngine.OnEntryRejected(string reason)` and T7 already wired it. T27 therefore *removes* working behaviour.
**Fix:** T27's `OnEntryRejected(BbEntryEngine, string)` gets the `else if (_engine != null) _engine.OnEntryRejected(reason);` arm and the comment is deleted.

**14. T49's failing-test preconditions are already satisfied by T7 and T8.**
T49 Step 2 expects `grep -c "OnEntryRejected" ninjascript/BreakBoxStrategy.cs` → `0` and `grep -n "degenerate stop"` → a hit at `:471`. After T7 the count is 4 (T7's own Step 4 asserts exactly that) and after T8 the degenerate-stop guard is gone. T49's "Step 2: verify it fails" cannot fail.
**Fix:** delete T49's B14 half (already done by T8) and rewrite Step 1/2 as a check that the *box* arm of the router is present: `grep -c '_engine.OnEntryRejected\|_engine.OnTriggerExpired' ninjascript/BreakBoxStrategy.cs` → expect 2.

**15. T80's "one implementation" claim is unachievable in Phase 5.**
T80 Produces: "the cloud engine's own gate (c) should call it too so the picture and the engine cannot drift." `BbCloud` is written in Phase 2, three phases earlier, and no Phase 2 task references `BbMath.CloseInRange`. As planned, Phase 2 hand-rolls the ratio and Phase 5 adds a second copy — exactly the drift T80 exists to prevent.
**Fix:** move T80 into Phase 1 (as a sibling of T4), and make the Phase 2 cloud gate (c) call `BbMath.CloseInRange(bar, dir) >= cfg.CloseInRange` explicitly in its Produces.

---

## D. Duplicated work — same lines, same reason, in different phases

**16. B4/B6/B7 shell plumbing is written three times: T7, T27, T49.**
All three rewrite `SubmitEntry`'s `qty < 1` guard, `AgeWorkingEntry`, `CancelWorkingEntry`, the `OrderState.Rejected` entry arm, and add an `OnEntryRejected(BbEntryEngine, string)` router. They disagree on details:
- rejection reason string for the same branch: `"order_rejected"` (T7) / `"platform_rejected"` (T27) / `"broker_rejected"` (T49);
- `CancelWorkingEntry(string why, bool engineDisarmed)` overload (T7) vs single-arg (T27, T49);
- expiry routing: inline in `CancelWorkingEntry` (T7) vs `if (why == "expired") OnTriggerExpiredFor(_owningEngine);` (T49) — `OnTriggerExpiredFor` is a name in neither the contract nor T7.
**Fix:** T7 is the single owner of the shell refusal plumbing. Delete Steps 3–7 of T27 (keep only the arbitration block and `_owningEngine = a.Engine` on submit) and delete T49's `SubmitEntry`/`AgeWorkingEntry`/`CancelWorkingEntry`/Rejected-branch edits; T49 shrinks to "point T7's router at `_engine.OnEntryRejected` / `_engine.OnTriggerExpired`". Standardise on `"order_rejected"` and on T7's two-argument `CancelWorkingEntry`.

**17. T7's `_entryFromEngine` is silently dropped by T27 and T49.**
T7 adds `private bool _entryFromEngine;` so a hand-clicked `PanelManualEntry` (which owns no token) cannot refund an arm to an engine that never armed. T27's and T49's `CancelWorkingEntry` have no such guard: a cancelled manual entry calls `OnEntryRejected(_owningEngine, why)` and refunds `ArmsUp`/`ArmsDn` on a box that was never armed.
**Fix:** whichever version survives item 16 must keep the `if (!_entryFromEngine) return;` guard and the `_entryFromEngine = false;` in `PanelManualEntry`.

**18. B5 (panel toggles off the WPF thread) is fixed twice: T11 and T67.**
T11 edits the five v1 handlers to `Dispatch(o => BuildConfigs())`. T64/T67 rewrite the whole panel and reintroduce the same fix under a different name, `Rebuild()`. T11's grep gate also assumes `_retraceBtn` exists — T40 deletes it, so T11's "five hits" becomes four if T40 lands first.
**Fix:** delete T11; move its `Dispatch`-vs-`BuildConfigs` comment (the `BreakBoxPanel.cs:16-18` header correction) into T67, and standardise on `Rebuild()`.

**19. B2 (`canTrade` into `OnBar`) is done twice: T5 and T40.**
T5 changes the signature and the `:385-394` call site. T40's Step 3 re-writes `:385-394` with the same edit and the same B2 comment.
**Fix:** T40 drops the `:385-394` edit from its file list and Step 3; it only rewrites `BreakBoxCore.cs`.

**20. T40 discards T5's and T6's tests.**
"Replace `tests/BoxTests.cs` entirely" deletes `CanTradeGatesArmingOnly` (T5) and `TriggerClockAndRefusals` (T6) — the only asserts for B2 and B6 — while their commits claim those bugs are pinned. T5's mechanical `sed` migration of the old call sites is also wasted.
**Fix:** either move T5's and T6's asserts into `tests/ShellTests.cs` (they exercise engine behaviour, not the slot machine, so they survive the rewrite once `Cfg()` drops `BoxSource`/`HtfMinutes`), or state in T40 that B2/B6 coverage is re-established by T46/T47 and delete the "an assert pins that" language from T5/T6's commit bodies.

**21. `BarSeconds()` is implemented twice with different constants.**
T1/T2/T3 build `BbScale.FallbackSeconds = 30`, `MinEstimateSamples = 200`, `EstimateBarSeconds` (median, `count > gapSecs.Length` guard, sub-second floor), and a `BarSeconds()` that uses them. T81's Vision hand-rolls its own: hardcoded `30`, hardcoded `200`, a `g > 0.0 && g < 3600.0` filter, `Math.Max(2, RibbonFastSec / b)` instead of `BbScale.Bars`. T1's Produces says "Every later phase converts its seconds dials through this and nothing else."
**Fix:** T81's `BarSeconds()` calls `BbScale.EstimateBarSeconds` / `BbScale.FallbackSeconds` / `BbScale.MinEstimateSamples`, and every line of its `BuildConfigs()` becomes `BbScale.Bars(XSec, b, floor)`.

---

## E. Gate ladder — the panel and the engines disagree numerically

**22. T40/T46 report ten ladder depths; T65 renders six rows with different names.**
T40 declares "0 atr warm · 1 box warming · 2 box · 3 box valid · 4 auto-trade · 5 window · 6 budget · 7 armed · 8 break · 9 arms"; T46 emits depths 0,1,2,3,4,4,5,6,7,8,9,9. T65 declares `private const int GateRows = 6;` and `BoxGates = { "atr warm", "box sealed", "validity", "edge break", "window", "budget" }`. Consequences in T69's `ApplySnap`: with `depth == 7` ("armed"), `RowState(i, 7)` returns 0 for all six rendered rows → the panel paints six green OKs while the engine is blocked. With `depth == 3` the panel labels the blocker "edge break" when the engine blocked on box validity.
**Fix:** set `GateRows = 10` and `BoxGates = { "atr warm", "box warming", "box", "box valid", "auto-trade", "window", "budget", "armed", "break", "arms" }` — the exact strings T45/T46 pass to `Gate.Set`. Then T69's `s.GateVal[1]`/`[2]` cloud-specific fills must be re-indexed against the cloud ladder.

**23. The cloud ladder's depth→name mapping is produced by nobody.**
T65 asserts `CloudGates = { "atr warm", "regime", "token", "gold candle", "window", "budget" }` and T69 fills `GateVal[1]` with regime and `GateVal[2]` with token on that assumption. No Phase 2 task's Produces states which integer `BbCloud` passes as the third argument of `Gate.Set` for each gate.
**Fix:** add the cloud gate ladder (index → block string) to the Phase 2 cloud-engine task's Produces block, and derive `CloudGates` from it rather than the reverse.

**24. T46 emits two different blocks at the same depth 4.**
`_st.Gate.Set("engine", "box engine off", 4)` and `_st.Gate.Set("auto-trade", "off or locked out", 4)`. T40's ladder has no "engine" row, so `BoxGates[4]` can only be one of them and the panel mislabels the other.
**Fix:** give the engine-off gate its own index (insert at 4, shift auto-trade→5, window→6, budget→7, armed→8, break→9, arms→10, `GateRows = 11`), or fold it into the auto-trade row: `_st.Gate.Set("auto-trade", "box engine off", 4)`.

---

## F. Test-runner and gate wiring

**25. Three tasks rewrite `Program.Main`'s run list and each drops the others' registrations.**
T1: `BoxTests, BracketTests, ShellTests`. T60: `BoxTests, BracketTests, HistoryTests` (drops `ShellTests`). T80: `BoxTests, BracketTests, VisionTests` (drops `ShellTests` *and* `HistoryTests`). Applied in order, ~40 asserts stop running with no failure to show for it.
**Fix:** each of T60 and T80 inserts one line rather than replacing the block — T60 adds `HistoryTests.Run();` after `ShellTests.Run();`, T80 adds `VisionTests.Run();` after `HistoryTests.Run();`.

**26. `check.sh` is red between T46 and T47 — the gate is the gate.**
T46 Step 4 expects FAIL ("`FAIL expiry does not re-arm inside the cooldown`") and Step 5 deliberately skips the commit. House rules make `scripts/check.sh` the gate; a staged-but-uncommitted red tree is a task that cannot be reviewed or reverted independently.
**Fix:** merge T46 and T47 into one task, or move the three expiry asserts (`"expiry does not re-arm inside the cooldown"`, the `"cooldown"` gate check, and the arms-count follow-ons) out of T46's `ArmingDoesNotSpendTheEdge` into T47's `ExpirySpendsAnArmRejectionRefundsIt`.

---

## G. Placeholders and unfulfilled forward references

**27. T6 promises a "bounded restore" on expiry that T47 explicitly refuses to implement.**
T6's `OnTriggerExpired` comment: "§6.2's arms-per-edge budget replaces the latch and gives expiry a BOUNDED restore; until then, expiry keeps the edge spent." T47 ships the opposite: "The market declined a live trigger. That IS an attempt, so the arm stays spent." Nothing between them delivers a restore.
**Fix:** change T6's comment to "expiry keeps the edge spent, and still does after §6.2 — the bounded budget is `BoxArmsPerEdge`, not a refund."

**28. T85's Consumes lists eleven `BbConfig` box fields "per spec §6.3 after conversion" without naming the conversion.** They match T40 by luck; `BoxSampleN` and `BoxMeanSamples` are passed straight through in both T48 and T85 while `BoxLookback`/`BoxMaxAge`/`BoxArmCooldown` are converted. That distinction is stated nowhere in T85, which is the task most likely to be executed by someone who has not read T48.
**Fix:** T85's Consumes names which three convert and which eight pass through, quoting T48.

---

## H. House-rule checks

**29. Bar-valued NinjaScriptProperties survive the plan.**
`BoxSampleN` (T48, `[Range(20, 2000)]`, default 200; T85, `[Range(20, 5000)]`) is a lookback horizon counted in bars — 200 bars is 100 min at 30s and 200 min at 1m, so the percentile denominator silently changes meaning with bar size, which is the exact failure §8 exists to prevent. `AtrPeriod`, `MaPeriod`, `E50Period` and `SwingStrength` are likewise bar-valued and no task converts them, while T3's conformance grep only checks the single token `TriggerLifeBars`.
**Fix:** either add `BoxSampleSec` → `BbScale.Bars(BoxSampleSec, _barSec, 20)` in T48/T85, or state the exemption explicitly in T48 the way `BoxMinBars` is exempted ("a confirmation COUNT, not a horizon") — `BoxSampleN` does not qualify for that exemption as written. Widen T3's grep gate to `grep -nE '(Bars|Period|Lookback|MaxAge|Cooldown|SampleN)\s*\{\s*get' ninjascript/BreakBoxStrategy.cs` and list the accepted exemptions.

**30. No pure file does I/O and no pure file imports NinjaTrader — verified clean.**
`BreakBoxHistory.cs` (T60–T62, T66, T68) contains only `System`, `System.Collections.Generic`, `System.Globalization`, `System.Text`, `System.Security.Cryptography`; the `File.*` calls live in `BreakBoxStrategy.cs` (T63). `BreakBoxVision.cs` (T81) is correctly *not* added to `tests/BreakBox.Tests.csproj`.

---

## I. Deploy tasks

**31. T28 copies four files; the repo has five (six after T60), and two of them were edited in Phases 1–2.**
T28's copy list is `BreakBoxTypes, BreakBoxCore, BreakBoxCloud, BreakBoxStrategy`. `BreakBoxPanel.cs` was edited by T7 (`PanelManualEntry` sets `_owningEngine`/`_entryFromEngine`) and T11; `BreakBoxExits.cs` by T10 (`StopSourceWarm`). Deploying without them leaves NT8 compiling a panel that references fields the deployed strategy has and an `_exitCfg` warm-up call that does not exist → CS0103 cascade on F5. T28 Step 1 also says "Expected: the four v1 files under `Strategies/`" — there are five.
**Fix:** T28 copies `BreakBoxTypes BreakBoxCore BreakBoxExits BreakBoxCloud BreakBoxStrategy BreakBoxPanel` and the `cmp` loop covers all six; Step 1's expectation reads "five v1 files".

**32. T86 will never find the files T63 writes.**
T63 names the file with `Instrument.FullName` → `BbHistory.FileName("MNQ 09-26", "Sim101")` → `history-MNQ_09-26-Sim101.jsonl` (pinned by T62's assert). T86 globs `"history-" + Instrument.MasterInstrument.Name + "-*.jsonl"` → `history-MNQ-*.jsonl`, which does not match. Every marker silently absent, and T87's step-4 check #5 ("gold bars vs blue arrows") reads as "the strategy took no trades".
**Fix:** T86's pattern becomes `"history-" + Instrument.FullName.Replace(' ', '_') + "-*.jsonl"`.

**33. Deploy paths disagree.** T28 uses `/mnt/c/Users/$USER/Documents/NinjaTrader 8/...`; T87 hardcodes `<Documents>/../...`. `$USER` under WSL is the Linux user, not the Windows one.
**Fix:** both use the same literal Windows path (`<Documents>/NinjaTrader 8/bin/Custom`).

---

## J. Smaller, still concrete

**34.** T63's `Canonical` list ends with `"bar=" + BarSeconds().ToString(...)` inside `BuildConfigs()`. T3's whole reason for caching `_barSec` is that "the estimate walks the loaded history and `BuildConfigs` runs again on every panel click." Use `_barSec`.

**35.** T69's `FillGates` uses `Mins(...)` which calls `BarSeconds()` once per gate row per bar — same problem, same fix (`_barSec`).

**36.** T7's `AgeWorkingEntry` logs `"engine:" + _engine.LastDisarmReason` (T6). T40's rewritten `BbEngine`/`BbEngineState` drop `LastDisarmReason` and `DisarmReason` entirely. If item 16's consolidation keeps T7's version, T40 must retain `public string LastDisarmReason { get; }`; if T49's version wins, T6's `DisarmReason`/`LastDisarmReason` should never be built.

**37.** T81 names the cloud dial `CloseInRangeMin`; Phase 2's strategy property is `CloseInRange` and `BbCloudConfig.CloseInRange` is the field. T87 asks the user to keep the two surfaces identical dial-for-dial. Rename Vision's to `CloseInRange`.

**38.** T5's migration `sed -i -E 's/, (atr|[0-9]+\.[0-9]+), true, false\)/, \1, true, true, false)/g' tests/BoxTests.cs` is applied to a file T40 deletes wholesale two phases later, and it will not match call sites written as `, 4.0, true, false)` where the ATR literal has no decimal (`, 4, true, false)`). Not worth fixing if item 20 is applied; if T5's tests move to `ShellTests.cs`, hand-edit instead.