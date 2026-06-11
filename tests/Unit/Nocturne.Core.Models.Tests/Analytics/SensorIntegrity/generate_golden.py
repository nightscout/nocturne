"""Generate golden-vector fixtures from the reference v6 detector.

Runs cgm_cluster_detector_v5 (the published algorithm) over the shipped sample
Clarity exports plus a set of crafted edge-case series, and dumps the full
results (clusters + hypo events, including debug fields) to a single JSON file.
The C# port asserts byte-for-byte parity against these fixtures.
"""
import json
import sys
from datetime import datetime, timedelta
from pathlib import Path

import pandas as pd

HERE = Path(__file__).resolve().parent
AI = HERE / "cgm_ai_assistant"
sys.path.insert(0, str(AI))

from cgm_cluster_detector_v5 import detect_clusters, find_hypo_events  # noqa: E402


def iso(ts):
    return pd.Timestamp(ts).strftime("%Y-%m-%dT%H:%M:%S")


def dump_clusters(clusters):
    out = []
    for c in clusters:
        d = c["debug"]
        out.append({
            "start": iso(c["start"]),
            "end": iso(c["end"]),
            "min": round(float(c["min"]), 6),
            "max": round(float(c["max"]), 6),
            "duration_min": round(float(c["duration_min"]), 6),
            "confidence": c["confidence"],
            "debug": {
                "dt_min": round(float(d["dt_min"]), 6),
                "win_pts": int(d["win_pts"]),
                "peak_reversals_window": round(float(d["peak_reversals_window"]), 6),
                "peak_incoh_ratio": round(float(d["peak_incoh_ratio"]), 6),
                "amp_val": round(float(d["amp_val"]), 6),
                "step_max": round(float(d["step_max"]), 6),
                "bumped": bool(d["bumped"]),
                "chain_size": int(d["chain_size"]),
                "chain_promoted": bool(d["chain_promoted"]),
            },
        })
    return out


def dump_events(events):
    out = []
    for e in events:
        c = e["cluster"]
        out.append({
            "cluster_start": iso(c["start"]),
            "cluster_end": iso(c["end"]),
            "cluster_confidence": c["confidence"],
            "nadir": round(float(e["nadir"]), 6),
            "nadir_time": iso(e["nadir_time"]),
            "time_to_nadir_hours": round(float(e["time_to_nadir_hours"]), 6),
            "n_readings_below_threshold": int(e["n_readings_below_threshold"]),
            "insulin_during_cluster": [
                {"time": iso(i["time"]), "units": round(float(i["units"]), 6)}
                for i in e["insulin_during_cluster"]
            ],
        })
    return out


def make_case(name, timestamps, glucose, hypo_kwargs=None,
              insulin_times=None, insulin_units=None):
    clusters = detect_clusters(timestamps, glucose)
    case = {
        "name": name,
        "input": {
            "timestamps": [iso(t) for t in timestamps],
            "glucose": [round(float(g), 6) for g in glucose],
        },
        "expected_clusters": dump_clusters(clusters),
    }
    if hypo_kwargs is not None:
        kwargs = dict(hypo_kwargs)
        if insulin_times is not None:
            kwargs["insulin_times"] = insulin_times
            kwargs["insulin_units"] = insulin_units
            case["input"]["insulin_times"] = [iso(t) for t in insulin_times]
            case["input"]["insulin_units"] = [round(float(u), 6) for u in insulin_units]
        events = find_hypo_events(timestamps, glucose, **kwargs)
        case["hypo_kwargs"] = {k: v for k, v in hypo_kwargs.items()}
        case["expected_events"] = dump_events(events)
    return case


# ---- synthetic series builders -------------------------------------------------

def ramp(start, n, step_min=5, lo=80, slope=2.0):
    ts = [start + timedelta(minutes=step_min * i) for i in range(n)]
    g = [lo + slope * i for i in range(n)]
    return ts, g


def flat(start, n, step_min=5, val=120):
    ts = [start + timedelta(minutes=step_min * i) for i in range(n)]
    g = [val for _ in range(n)]
    return ts, g


def oscillate(start, n, step_min=5, base=120, amp=20):
    ts = [start + timedelta(minutes=step_min * i) for i in range(n)]
    g = [base + (amp if i % 2 == 0 else -amp) for i in range(n)]
    return ts, g


def build_cases():
    s = datetime(2026, 1, 1, 0, 0, 0)
    cases = []

    ts, g = ramp(s, 40)
    cases.append(make_case("monotonic_ramp", ts, g))

    ts, g = flat(s, 40)
    cases.append(make_case("flat_line", ts, g))

    # Sustained high-amplitude oscillation (5-min): strong incoherent cluster.
    ts, g = oscillate(s, 30, base=140, amp=25)
    cases.append(make_case("pure_oscillation_5min", ts, g))

    # Oscillation containing one >=40 mg/dL single step -> spike promotion.
    ts, g = oscillate(s, 24, base=150, amp=18)
    g[10] = g[10] + 45  # inject a single large jump
    cases.append(make_case("spike_promotion", ts, g))

    # Three separate oscillation bursts, each separated by a ~20-min reading gap. The
    # gap (>12 min) masks the windows that span it, cleanly ending each cluster, while
    # the ~20-min spacing stays inside the 30-min chain radius -> chain promotion fires.
    ts, g = [], []
    cursor = s
    for burst in range(3):
        bts, bg = oscillate(cursor, 10, base=130, amp=22)
        ts += bts
        g += bg
        cursor = bts[-1] + timedelta(minutes=20)  # reading gap between bursts
    cases.append(make_case("chain_promotion", ts, g))

    # Oscillation straddling a >12 min timestamp gap -> windows over the gap masked.
    ts1, g1 = oscillate(s, 10, base=140, amp=25)
    gap_start = ts1[-1] + timedelta(minutes=40)  # 40-min gap
    ts2, g2 = oscillate(gap_start, 10, base=140, amp=25)
    cases.append(make_case("gap_masking", ts1 + ts2, g1 + g2))

    # Coarse 15-min Libre-style sampling oscillation (exercises win_pts time floor).
    ts, g = oscillate(s, 20, step_min=15, base=150, amp=30)
    cases.append(make_case("coarse_15min_oscillation", ts, g))

    # Very short oscillation (<20 min) -> cluster dropped by min_cluster_minutes.
    ts, g = oscillate(s, 5, base=140, amp=25)
    cases.append(make_case("short_oscillation_dropped", ts, g))

    # Cluster followed by hypo within 3h.
    ts, g = oscillate(s, 24, base=150, amp=25)
    tail_start = ts[-1] + timedelta(minutes=5)
    tail_ts = [tail_start + timedelta(minutes=5 * i) for i in range(20)]
    tail_g = [max(55, 150 - 6 * i) for i in range(20)]  # descend into hypo
    cases.append(make_case(
        "cluster_then_hypo",
        ts + tail_ts, g + tail_g,
        hypo_kwargs={"min_confidence": "low"},
    ))

    # Same, with insulin dosed during the cluster + require_insulin.
    ins_t = [ts[6], ts[12]]
    ins_u = [2.5, 1.0]
    cases.append(make_case(
        "cluster_then_hypo_with_insulin",
        ts + tail_ts, g + tail_g,
        hypo_kwargs={"min_confidence": "low", "require_insulin": True},
        insulin_times=ins_t, insulin_units=ins_u,
    ))

    return cases


def load_clarity_egv(path):
    raw = pd.read_csv(path, low_memory=False)
    ts_col = next((c for c in raw.columns if "Timestamp" in c and "YYYY" in c), None) \
        or next((c for c in raw.columns if "Timestamp" in c), None)
    gcol = next((c for c in raw.columns if "Glucose Value" in c), None)
    egv = raw[raw["Event Type"] == "EGV"].copy()
    egv["dt"] = pd.to_datetime(egv[ts_col], errors="coerce")
    egv["glucose"] = pd.to_numeric(egv[gcol], errors="coerce")
    egv = egv.dropna(subset=["dt", "glucose"]).sort_values("dt").reset_index(drop=True)
    return list(egv["dt"]), list(egv["glucose"])


def build_real_cases():
    cases = []
    for fname, label in [
        ("Clarity_Export_JaneDoe_2025-12-10.csv", "clarity_janedoe"),
        ("Clarity_Export_Heller_Daniel_2026-03-18.csv", "clarity_heller"),
    ]:
        ts, g = load_clarity_egv(AI / fname)
        cases.append(make_case(label, ts, g, hypo_kwargs={"min_confidence": "medium"}))
    return cases


if __name__ == "__main__":
    # The committed fixture holds only the synthetic cases: they are tiny, fully
    # self-contained (inputs embedded), and deterministically exercise every branch
    # of the detector. The large real Clarity datasets are run as a local sanity
    # check (pass --real) but not committed: embedding ~25k readings each bloats the
    # repo, and re-parsing the CSVs in C# would test CSV parsing quirks (non-stable
    # sort, duplicate timestamps) rather than the algorithm.
    synthetic = build_cases()
    payload = {
        "generator": "cgm_cluster_detector_v5 (release v6.0)",
        "config": "DetectorConfig defaults",
        "cases": synthetic,
    }
    out_path = HERE / "sensor_integrity_golden.json"
    out_path.write_text(json.dumps(payload, indent=2))
    for c in synthetic:
        nev = len(c.get("expected_events", []))
        print(f"{c['name']:32s} clusters={len(c['expected_clusters']):3d} events={nev}")
    print(f"\nwrote {out_path}  ({out_path.stat().st_size} bytes)")

    if "--real" in sys.argv:
        print("\n-- real Clarity datasets (local sanity check, not committed) --")
        for c in build_real_cases():
            print(f"{c['name']:32s} clusters={len(c['expected_clusters']):3d} "
                  f"events={len(c.get('expected_events', []))}")
