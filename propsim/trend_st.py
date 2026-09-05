#!/usr/bin/env python3
"""TrendST -- cloud-regime pullback with volume decay and an engulfing close.

Port of ninjascript/TrendST.cs (shell) + TrendStCore.cs (setup) +
BreakBoxCloud.cs (BbCloud regime engine, "Engine A") + BreakBoxTypes.cs
(WilderAtr, Ema). Long side; short is the exact mirror, same as the source.

THE SETUP (mirrors TrendStCore.cs's own header comment):
  1. Regime = the SAME BbCloud engine BreakBoxVision paints (EMA ribbon
     300s/690s, trend line 1560s, LATCHED). Only BbCloudState.RegimeLatched is
     read here -- the cloud's own token/trigger machinery (pullback-to-far-edge
     arming, GoldCandle, stop pricing) is irrelevant to TrendST and is not
     ported; canTrade=False in the C# caller confirms it is never exercised.
  2. A pullback: >= min_pullback_bars consecutive CLOSED bars whose low
     touches or enters the fast ribbon (regime up) or whose high does (regime
     down). The bar just before the first pullback bar is the "tip".
  3. Volume decays from the tip to the end of the pullback: the LAST pullback
     bar's volume < volume_decay_ratio x the TIP's volume. Endpoints only.
  4. The signal bar: a REAL engulfing bar -- its body covers the whole body of
     the previous bar AND it closes beyond the previous bar's extreme in the
     regime's direction. Entry is at its close.

TF_SECS = 30           # NT8 chart is 30-second bars (TrendST.cs header comment)
SESSION: RTH, 09:30-16:00 ET (full_session=False); the strategy's own window
defaults to 09:35-15:50 with a 16:00 flatten, comfortably inside RTH.

NO LOOKAHEAD: the whole state machine (ATR, the three EMAs, the regime latch,
the pullback/volume-decay/engulfing detector) is a single forward pass over
CLOSED bars, exactly mirroring OnBarUpdate's own bar-by-bar update order.
Every field of an accepted entry is frozen at the signal bar's close; the only
thing read from later bars is which tick the NEXT bar's open print landed on
(the house convention for a market order sent at OnBarClose -- see
`momentum_break` in engine.py's plugin template) and, purely to know when the
strategy is free to take a NEW entry, when that first entry's stop, target or
the session flatten resolves it (`_resolve_exit_ts` below). The truncation
selfcheck holds this invariant: cutting the tape right after the entry fill
does not change that entry's own fields.

FIDELITY -- every delta from the live NT8 strategy:
  * ATM mode (UseAtmStrategy / AtmTemplateName) is NOT ported. It is a live-only
    NT8 feature (AtmStrategyCreate, a broker-side bracket template) with no
    tick-tape equivalent; this port always uses the fixed tick stop/target path
    (StopTicks/TargetTicks), which is also TrendST's own default (UseAtmStrategy
    = false).
  * SetStopLoss/SetProfitTarget are placed ONCE per trade in the live strategy
    and never re-issued, specifically so a human can drag them in Chart Trader
    afterwards. Nothing drags them here: every trade's stop/target are the
    fixed tick distance from the fill, for the whole trade, exactly as if
    nobody touched Chart Trader.
  * IsExitOnSessionCloseStrategy / ExitOnSessionCloseSeconds=30 (a 30-second
    grace window NT8 gives itself to flatten at the session close) is not
    separately modelled -- a still-open position is resolved at the exact
    flatten_hhmm second, not up to 30s later. Session governs 09:30-16:00 RTH
    tapes only, so this never runs into an overnight gap.
  * "One position at a time" (the live strategy's `flat` check, gated on
    Position.MarketPosition) is approximated with a `busy_until` timestamp:
    each accepted entry's stop/target/flatten resolution is computed
    immediately (pessimistic tie -> the stop) purely to know when the next
    entry may be taken. This is bookkeeping for the entry GATE only -- the
    actual fill/stop-out/target-out accounting for P&L is still the PropSim
    engine's own job, exactly as with every other plugin.
  * `bars["t"]` is documented as each bar's FIRST tick (its open print); NT8's
    own `Time[0]` inside OnBarUpdate is the bar's CLOSE print. Window/flatten
    gating here reads the bar's LAST tick (`tape["ts"][bars["end"][i]-1]`) to
    match that convention; this is exact, not an approximation, but the
    day-of-bar check (whether bar i+1 is the same session as bar i, needed to
    refuse an entry that would leap across an overnight/weekend gap) uses the
    bar's OPEN time instead, the `momentum_break` template's own convention --
    the two agree on every bar that does not itself straddle a session
    boundary, which a 30-second bar never does on an RTH tape.
  * `contracts` is mirrored from the NT8 property list for parity but is not
    consumed by `entries()` -- this backtest engine sizes trades on its own,
    the same convention PullbackZone's port documents for its own inert dials.
"""
import numpy as np

# The PropSim sandbox hands a plugin `Strategy`, `Param`, `np` and `tp` in its
# namespace instead of letting it import them. Standalone (running this file
# for its selfchecks) they are absent, so stand-ins are defined -- same trick
# as pullback_zone.py, verified against plugins.ast_check: an import inside a
# try/except is still an import node to the AST walk, so this has to be a bare
# NameError probe, never a guarded import.
try:
    Strategy
except NameError:
    class Strategy:                      # pragma: no cover - sandbox stand-in
        tick = 0.25
        point_value = 20.0
        full_session = False

    class Param:                         # pragma: no cover - sandbox stand-in
        def __init__(self, default, lo, hi, desc, fixed=False):
            self.default, self.lo, self.hi = default, lo, hi
            self.desc, self.fixed = desc, fixed

try:
    tp
except NameError:
    class tp:                            # pragma: no cover - sandbox stand-in
        # tape.py's own constants/formulas (day_index, sec_of_day), reproduced
        # so a standalone run behaves identically to the sandboxed one.
        TPS = 10_000_000
        _NET_EPOCH_S = 62135596800

        @staticmethod
        def day_index(ts):
            return (np.asarray(ts) // tp.TPS - tp._NET_EPOCH_S) // 86400

        @staticmethod
        def sec_of_day(ts):
            return (np.asarray(ts) // tp.TPS - tp._NET_EPOCH_S) % 86400

TICK = 0.25
TF_SECS = 30                            # this strategy's fixed NT8 bar size

# BbCloudConfig.RefLookbackBars -- an internal C# const, not a parameter: the
# slope gate is normalised against a fixed 10-bar reference so it means the
# same thing at every bar size (§8 of BreakBoxCloud.cs's own header).
_REF_LOOKBACK_BARS = 10.0

_EMPTY4 = (np.array([], np.int64), np.array([], np.int8),
           np.array([]), np.array([]))


def _bars_from_secs(secs, tf_secs=TF_SECS, min_bars=2):
    """BbScale.Bars(horizonSecs, barSec, min) -- a seconds horizon on THIS
    strategy's fixed bar size, floored at `min_bars`."""
    n = int(secs) // tf_secs
    return n if n >= min_bars else min_bars


def _hhmm_secs(hhmm):
    """BbMath.HhmmToSecs: 935 -> 34200."""
    hhmm = int(hhmm)
    return (hhmm // 100) * 3600 + (hhmm % 100) * 60


def _in_window(secs, start, end):
    """BbMath.InWindow -- a wrap-at-midnight window. start==end is empty, not
    "always"; start>end wraps (never happens with this strategy's own
    defaults, but the source guards it and so does the port)."""
    if start == end:
        return False
    if start < end:
        return start <= secs < end
    return secs >= start or secs < end


def _flatten_cutoff_ts(tape_ts, tape_day, tape_sod, day, flatten_secs):
    """The timestamp of the first tick on `day` at/after `flatten_secs`, or
    that day's LAST tick if the tape never reaches it (a day that ends before
    flatten -- should not happen on an RTH tape, but fails safe rather than
    raising). `tape_day`/`tape_sod` are precomputed once per run; `tape_day` is
    monotone non-decreasing (tick time never runs backwards), which is what
    lets both searches be a binary search rather than a scan.
    """
    lo = int(np.searchsorted(tape_day, day, side="left"))
    hi = int(np.searchsorted(tape_day, day, side="right"))
    if hi <= lo:
        return int(tape_ts[-1])
    k = int(np.searchsorted(tape_sod[lo:hi], flatten_secs, side="left"))
    idx = lo + k if k < (hi - lo) else hi - 1
    return int(tape_ts[idx])


def _resolve_exit_ts(tape_ts, tape_px, entry_idx, direction, stop_px, target_px,
                     cutoff_ts):
    """When does THIS fill's position go flat -- at its stop, its target, or
    the session flatten? Pessimistic: a tie (both cross on the same tick)
    resolves to the stop, same convention as pullback_zone.py's own
    `_resolve_exit`. Used only to gate the NEXT entry (see FIDELITY); the
    engine resolves the real outcome independently.
    """
    end = int(np.searchsorted(tape_ts, cutoff_ts, side="right"))
    seg = tape_px[entry_idx + 1:end]
    if len(seg) == 0:
        return int(cutoff_ts)
    if direction > 0:
        s = np.flatnonzero(seg <= stop_px)
        t = np.flatnonzero(seg >= target_px)
    else:
        s = np.flatnonzero(seg >= stop_px)
        t = np.flatnonzero(seg <= target_px)
    si = int(s[0]) if len(s) else len(seg)
    ti = int(t[0]) if len(t) else len(seg)
    k = min(si, ti)
    if k == len(seg):
        return int(cutoff_ts)
    return int(tape_ts[entry_idx + 1 + k])


class TrendST(Strategy):
    """Cloud-regime pullback with volume decay and an engulfing close.
    Fixed tick stop/target (ATM mode is a live-only feature -- see FIDELITY)."""

    name, label = "trend_st", "TrendST (cloud pullback, volume decay, engulfing)"
    uses_ticks = True
    full_session = False                # RTH only, 09:30-16:00 ET

    # PARAMETER NAMES ARE THE NT8 PROPERTY NAMES IN SNAKE_CASE, defaults are
    # the C# State.SetDefaults values, and EVERY ONE IS FIXED: this is a
    # faithful, untuned port -- the lead runs any tuning separately.
    params = {
        "contracts": Param(1, 1, 50, "position size, contracts -- NOT sized by "
                                     "this backtest engine (see FIDELITY)",
                           fixed=True),
        "stop_ticks": Param(70, 1, 2000, "initial stop, ticks from entry",
                            fixed=True),
        "target_ticks": Param(145, 1, 4000, "initial target, ticks from entry",
                              fixed=True),
        "entry_start_hhmm": Param(935, 0, 2359, "entry window start, ET HHMM",
                                  fixed=True),
        "entry_end_hhmm": Param(1550, 0, 2359, "entry window end, ET HHMM",
                                fixed=True),
        "flatten_hhmm": Param(1600, 0, 2359, "flatten everything at this ET "
                                             "HHMM", fixed=True),
        "min_pullback_bars": Param(2, 1, 50, "closed bars touching the ribbon "
                                             "before a signal can fire",
                                   fixed=True),
        "max_pullback_bars": Param(10, 1, 200, "a pullback longer than this is "
                                               "dropped", fixed=True),
        "volume_decay_ratio": Param(0.8, 0.05, 1.0, "last pullback bar's volume "
                                                     "must be below this x the "
                                                     "tip's volume", fixed=True),
        "ribbon_fast_sec": Param(300, 30, 100000, "fast ribbon EMA horizon, "
                                                  "seconds", fixed=True),
        "ribbon_slow_sec": Param(690, 30, 100000, "slow ribbon EMA horizon, "
                                                  "seconds", fixed=True),
        "trend_line_sec": Param(1560, 30, 100000, "trend line EMA horizon, "
                                                   "seconds", fixed=True),
        "trend_slope_sec": Param(300, 30, 100000, "trend-slope lookback, "
                                                  "seconds", fixed=True),
        "trend_slope_atr": Param(0.15, 0.0, 10.0, "regime slope threshold, x "
                                                  "ATR over a 10-bar reference",
                                 fixed=True),
        "regime_memory_sec": Param(900, 30, 100000, "regime latch memory, "
                                                     "seconds", fixed=True),
        "atr_period": Param(14, 1, 500, "ATR period, bars", fixed=True),
    }

    def entries(self, bars, tape, p):
        o, h, l, c, v, t = (bars["o"], bars["h"], bars["l"], bars["c"],
                            bars["v"], bars["t"])
        n = len(c)
        if n < 3 or len(tape["ts"]) < 2:
            return _EMPTY4

        tick = self.tick
        atr_period = int(p["atr_period"])
        fast_bars = _bars_from_secs(p["ribbon_fast_sec"])
        slow_bars = _bars_from_secs(p["ribbon_slow_sec"])
        trend_bars = _bars_from_secs(p["trend_line_sec"])
        slope_look = _bars_from_secs(p["trend_slope_sec"])
        regime_mem_bars = _bars_from_secs(p["regime_memory_sec"])
        slope_atr = float(p["trend_slope_atr"])
        decay_ratio = float(p["volume_decay_ratio"])
        min_pb = int(p["min_pullback_bars"])
        max_pb = int(p["max_pullback_bars"])
        stop_ticks = float(p["stop_ticks"])
        target_ticks = float(p["target_ticks"])
        start_secs = _hhmm_secs(p["entry_start_hhmm"])
        end_secs = _hhmm_secs(p["entry_end_hhmm"])
        flatten_secs = _hhmm_secs(p["flatten_hhmm"])

        alpha_f = 2.0 / (fast_bars + 1.0)
        alpha_s = 2.0 / (slow_bars + 1.0)
        alpha_t = 2.0 / (trend_bars + 1.0)

        # Bar-level time: window/flatten gating reads the bar's CLOSE print
        # (matches NT8's Time[0]); the day-of-bar check reads its OPEN print
        # (the momentum_break template's own convention) -- see FIDELITY.
        close_ts = tape["ts"][bars["end"] - 1]
        close_sod = tp.sec_of_day(close_ts)
        bar_day = tp.day_index(t)

        tape_ts, tape_px = tape["ts"], tape["px"]
        tape_day = tp.day_index(tape_ts)
        tape_sod = tp.sec_of_day(tape_ts)

        atr_n, atr_val, atr_prev_close = 0, 0.0, 0.0
        ema_f_n = ema_s_n = ema_t_n = 0
        ema_f_val = ema_s_val = ema_t_val = 0.0

        slope_ring = np.zeros(slope_look + 1)
        slope_idx, slope_filled = 0, 0

        regime_latched, regime_latched_age = 0, 0

        setup_n, setup_dir = 0, 0
        vols = np.zeros(max_pb + 1)
        have_prev = False
        prev_o = prev_h = prev_l = prev_c = prev_v = 0.0

        busy_until = -1
        et_list, dr_list, st_list, tg_list = [], [], [], []

        for i in range(n):
            bo, bh, bl, bc, bv = float(o[i]), float(h[i]), float(l[i]), \
                                 float(c[i]), float(v[i])

            # --- WilderAtr.Update (BreakBoxTypes.cs) ---
            if atr_n == 0:
                tr = bh - bl
            else:
                tr = max(bh - bl, max(abs(bh - atr_prev_close),
                                      abs(bl - atr_prev_close)))
            atr_val = ((atr_val * atr_n + tr) / (atr_n + 1) if atr_n < atr_period
                      else atr_val + (tr - atr_val) / atr_period)
            atr_prev_close = bc
            atr_n += 1
            atr_warm = atr_n >= atr_period and atr_val > 0.0

            # --- Ema.Update x3 (seeded with the first sample, not an SMA) ---
            ema_f_val = bc if ema_f_n == 0 else alpha_f * bc + (1 - alpha_f) * ema_f_val
            ema_f_n += 1
            ema_s_val = bc if ema_s_n == 0 else alpha_s * bc + (1 - alpha_s) * ema_s_val
            ema_s_n += 1
            ema_t_val = bc if ema_t_n == 0 else alpha_t * bc + (1 - alpha_t) * ema_t_val
            ema_t_n += 1

            warm = (atr_warm and ema_f_n >= fast_bars and ema_s_n >= slow_bars
                   and ema_t_n >= trend_bars)

            # --- BbCloud regime latch (BreakBoxCloud.cs UpdateRegime) ---
            # The slope ring is pushed EVERY bar, unconditionally -- gating it
            # behind the warmup gate is how the ring never fills (its own
            # comment, verbatim reason).
            slope_idx = (slope_idx + 1) % len(slope_ring)
            slope_ring[slope_idx] = ema_t_val
            if slope_filled < len(slope_ring):
                slope_filled += 1
            cloud_warm = atr_warm and slope_filled >= len(slope_ring)
            if cloud_warm:
                ago = slope_ring[(slope_idx - slope_look) % len(slope_ring)]
                slope = (ema_t_val - ago) / slope_look
                need = slope_atr * atr_val / _REF_LOOKBACK_BARS
                now = 0
                if (bc > ema_t_val and ema_f_val > ema_s_val
                        and ema_s_val > ema_t_val and slope >= need):
                    now = 1
                elif (bc < ema_t_val and ema_f_val < ema_s_val
                        and ema_s_val < ema_t_val and slope <= -need):
                    now = -1
                if now != 0:
                    regime_latched, regime_latched_age = now, 0
                else:
                    regime_latched_age += 1
                    if regime_latched != 0:
                        closed_through = ((regime_latched > 0 and bc < ema_t_val)
                                          or (regime_latched < 0 and bc > ema_t_val))
                        if closed_through or regime_latched_age > regime_mem_bars:
                            regime_latched, regime_latched_age = 0, 0
            regime = regime_latched if warm else 0

            # --- TrendStSetup.OnBar (TrendStCore.cs), verbatim control flow ---
            fire = 0
            prev_had = have_prev
            p_o, p_h, p_l, p_c, p_v = prev_o, prev_h, prev_l, prev_c, prev_v
            prev_o, prev_h, prev_l, prev_c, prev_v = bo, bh, bl, bc, bv
            have_prev = True

            process = True
            if regime == 0 or regime != setup_dir:
                setup_n = 0
                setup_dir = regime
                if regime == 0:
                    process = False        # C#'s early `return 0`

            if process:
                touches = (bl <= ema_f_val) if regime > 0 else (bh >= ema_f_val)
                body = (bc > bo) if regime > 0 else (bc < bo)
                engulf = False
                if prev_had:
                    body_lo, body_hi = min(p_o, p_c), max(p_o, p_c)
                    if regime > 0:
                        engulf = bo <= body_lo and bc >= body_hi and bc > p_h
                    else:
                        engulf = bo >= body_hi and bc <= body_lo and bc < p_l

                if setup_n >= min_pb and body and engulf:
                    if vols[0] > 0 and vols[setup_n] < decay_ratio * vols[0]:
                        fire = regime
                    setup_n = 0
                elif touches:
                    started_without_prev = False
                    if setup_n == 0:
                        if not prev_had:
                            started_without_prev = True
                        else:
                            vols[0] = p_v
                    if not started_without_prev:
                        if setup_n >= max_pb:
                            setup_n = 0
                        else:
                            setup_n += 1
                            vols[setup_n] = bv
                else:
                    setup_n = 0

            # --- entry gating (OnBarUpdate's flatten/window/flat checks) ---
            if fire != 0:
                secs = int(close_sod[i])
                has_next = i + 1 < n
                if (secs < flatten_secs and _in_window(secs, start_secs, end_secs)
                        and has_next and bar_day[i + 1] == bar_day[i]):
                    entry_idx = int(bars["start"][i + 1])
                    entry_ts = int(tape_ts[entry_idx])
                    if entry_ts > busy_until:
                        fill = float(tape_px[entry_idx])
                        stop = fill - fire * stop_ticks * tick
                        target = fill + fire * target_ticks * tick
                        et_list.append(entry_idx)
                        dr_list.append(fire)
                        st_list.append(stop)
                        tg_list.append(target)
                        cutoff_ts = _flatten_cutoff_ts(
                            tape_ts, tape_day, tape_sod, bar_day[i + 1], flatten_secs)
                        busy_until = _resolve_exit_ts(
                            tape_ts, tape_px, entry_idx, fire, stop, target, cutoff_ts)

        if not et_list:
            return _EMPTY4
        return (np.array(et_list, np.int64), np.array(dr_list, np.int8),
                np.array(st_list), np.array(tg_list))


# ---------------------------------------------------------------- selfcheck
def _q(x):
    return round(x / TICK) * TICK


def _fx_bars(decayed=True):
    """Warmup uptrend (steady +2.0/bar ramp, long enough to warm ATR(14) and
    all three EMAs at their default horizons: fast=10, slow=23, trend=52
    bars), a 2-bar pullback into the fast ribbon with volume decaying from the
    tip, then a bullish engulfing signal bar. `decayed=False` inflates the
    LAST pullback bar's volume above the tip's -- the one thing that must
    flip the outcome, isolating the volume-decay gate.
    """
    b = []

    def one(o, hi, lo, cl, vol):
        b.append((_q(o), _q(hi), _q(lo), _q(cl), float(vol)))

    px = 100.0
    for _ in range(85):                 # warmup ramp: comfortably past 52+10
        o = px
        px += 2.0
        one(o, max(o, px) + 1.0, min(o, px) - 1.0, px, 500.0)

    # tip: the last ramp bar's volume (bar 84 above) is the tip's volume, 500.
    # pullback: 2 bars retracing ~18 points, low undercutting the fast ribbon
    # (which lags the ramp by only a few points), decaying volume 500 -> 300
    # -> 150 (each < 0.8x the previous, well under the tip too).
    one(px, px + 0.5, px - 9.0, px - 8.0, 300.0)      # pullback bar 1
    tip_low_2 = px - 8.0 - 10.0
    one(px - 8.0, px - 7.5, tip_low_2, px - 9.0,
        500.0 if not decayed else 150.0)              # pullback bar 2

    # signal: bullish engulfing of the LAST pullback bar's body (open<=its
    # low-body, close>=its high-body) closing above its high.
    prev_o, prev_c, prev_h = px - 8.0, px - 9.0, px - 7.5
    body_lo, body_hi = min(prev_o, prev_c), max(prev_o, prev_c)
    sig_o = body_lo - 0.5
    sig_c = prev_h + 3.0
    one(sig_o, sig_c + 0.5, sig_o - 0.5, sig_c, 400.0)

    # a few quiet bars afterwards so the entry (next bar's open) has somewhere
    # to sit and the flatten cutoff has ticks to search through.
    for _ in range(20):
        one(sig_c, sig_c + 1.0, sig_c - 1.0, sig_c, 200.0)
    return b


def _fx_tape(bars30, day0=20100, sod0=9 * 3600 + 40 * 60):
    """Four ticks per bar (open, both extremes in path order, close), all on
    one RTH session -- the fixture never needs a second day."""
    ts, px = [], []
    base = (int(day0) * 86400 + tp._NET_EPOCH_S + int(sod0)) * tp.TPS
    for k, (o, hh, ll, cc, _v) in enumerate(bars30):
        t0 = base + k * TF_SECS * tp.TPS
        mid = (hh, ll) if cc < o else (ll, hh)
        for dt, val in zip((0, 7, 14, 21), (o, mid[0], mid[1], cc)):
            ts.append(t0 + dt * tp.TPS)
            px.append(val)
    n = len(ts)
    return dict(ts=np.array(ts, np.int64), px=np.array(px, np.float64),
                vol=np.ones(n, np.int64), side=np.zeros(n, np.int8))


def _bars_dict(bars30, tape):
    o = np.array([r[0] for r in bars30])
    h = np.array([r[1] for r in bars30])
    l = np.array([r[2] for r in bars30])
    c = np.array([r[3] for r in bars30])
    v = np.array([r[4] for r in bars30])
    n = len(bars30)
    start = np.arange(n) * 4
    end = start + 4
    return dict(t=tape["ts"][start], o=o, h=h, l=l, c=c, v=v,
                start=start.astype(np.int64), end=end.astype(np.int64))


def _selfcheck_positive():
    bars30 = _fx_bars(decayed=True)
    tape = _fx_tape(bars30)
    bars = _bars_dict(bars30, tape)
    s = TrendST()
    p = {k: v.default for k, v in s.params.items()}
    et, dr, st, tg = s.entries(bars, tape, p)
    assert len(et) == 1, f"expected exactly one trade, got {len(et)}"
    assert dr[0] == 1, "the fixture is a bullish pullback -- must be a LONG"
    fill = tape["px"][et[0]]
    assert st[0] < fill < tg[0], (st[0], fill, tg[0])
    assert abs((fill - st[0]) / TICK - p["stop_ticks"]) < 1e-6
    assert abs((tg[0] - fill) / TICK - p["target_ticks"]) < 1e-6
    print("positive (long pullback + volume decay + engulfing) OK")
    return bars, tape, p, int(et[0])


def _selfcheck_negative():
    bars30 = _fx_bars(decayed=False)     # ONLY the volume-decay gate broken
    tape = _fx_tape(bars30)
    bars = _bars_dict(bars30, tape)
    s = TrendST()
    p = {k: v.default for k, v in s.params.items()}
    et, dr, st, tg = s.entries(bars, tape, p)
    assert len(et) == 0, ("volume did not decay from the tip -- must produce "
                          f"no trade, got {len(et)}")
    print("negative (volume did not decay -> no trade) OK")


def _selfcheck_truncation(bars, tape, p, entry_idx, st_full, tg_full):
    """No-lookahead invariant: every field of the entry is frozen at the
    signal bar's close and the fill bar's open. Cut the tape (and bars) right
    after the fill lands -- the recorded entry must not move."""
    fill_bar = int(np.searchsorted(bars["start"], entry_idx, side="right"))
    cut_bar = fill_bar                           # keep bars 0..fill_bar-1 (the fill bar included)
    cut_tick = int(bars["end"][fill_bar - 1])
    bars2 = {k: (v[:cut_bar] if k in ("t", "o", "h", "l", "c", "v", "start", "end")
               else v) for k, v in bars.items()}
    tape2 = {k: v[:cut_tick] for k, v in tape.items()}
    et2, dr2, st2, tg2 = TrendST().entries(bars2, tape2, p)
    assert len(et2) == 1, f"the truncated tape must still show the one entry, got {len(et2)}"
    assert et2[0] == entry_idx and dr2[0] == 1
    assert abs(st2[0] - st_full) < 1e-9 and abs(tg2[0] - tg_full) < 1e-9
    print("truncation invariant OK")


if __name__ == "__main__":
    bars, tape, p, entry_idx = _selfcheck_positive()
    _selfcheck_negative()
    et_full, dr_full, st_full, tg_full = TrendST().entries(bars, tape, p)
    _selfcheck_truncation(bars, tape, p, entry_idx, st_full[0], tg_full[0])
