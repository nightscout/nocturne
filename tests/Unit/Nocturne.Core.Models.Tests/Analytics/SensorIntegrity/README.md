# Sensor-integrity golden vectors

`sensor_integrity_golden.json` pins the expected output of `SensorIntegrityDetector` against the
**reference Python implementation** it was ported from, so the C# port is validated against an
external source of truth rather than against itself. `SensorIntegrityDetectorGoldenTests` loads the
fixture and asserts the detector reproduces it field-for-field.

## Source

- Reference: **argv01/cgm-sensor-integrity**, release **v6.0** — `cgm_cluster_detector_v5.py`
  (the published `detect_clusters` / `find_hypo_events` detector). License: MIT.
- The reference is **not** vendored into this repo. The fixture is the committed artifact.

## Regenerating (after an intentional algorithm change)

`generate_golden.py` builds the fixture from a set of crafted edge cases. It imports the reference
detector, which you must place alongside it first:

1. Download `cgm_ai_assistant.zip` from the v6.0 release and extract it into a `cgm_ai_assistant/`
   directory next to `generate_golden.py` (the script adds that directory to `sys.path`).
2. Create a venv and install deps: `python -m venv .venv && .venv/Scripts/python -m pip install pandas numpy`.
3. Run `python generate_golden.py` (add `--real` to also sanity-check against the bundled sample
   Clarity exports; those are not committed to the fixture).

The synthetic cases deterministically exercise every branch (oscillation, spike promotion,
chain promotion, gap-masking, coarse sampling, hypo, hypo+insulin), so the committed fixture stays
small and self-contained.
