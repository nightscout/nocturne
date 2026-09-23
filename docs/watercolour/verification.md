# Verification

What is tested where, what was verified in a browser, and the measured
performance numbers. The raw figures come from the infra README, the package
README and `docs/plans/2026-09-17-watercolour-plan.md`; see those for the
surrounding detail.

## Unit and integration tests

### `nocturne-watercolour-core` (std only; no GPU needed)

| Area | What is asserted |
|---|---|
| Validation | `Scene::validate()` returns all errors, typed; per-palette/per-artwork validation |
| Seeding | `Seed`/`SubSeed` purposes, splitmix64 stream, lattice hash |
| Paper | four octaves + pooling + fibre, normalised coordinates, aspect-aware generation |
| Palettes | role indexing, `for_dark_surface()`, `MAX_PIGMENTS` |
| Stamps | paper-driven edge break-up, seeded radius jitter, mask rasterisation |
| Kubelka-Munk | zero thickness is clear, unit thickness recovers `Rw`, alpha conversion exact over white and bounded over black, two glazes darker than one |
| Easing | `ease(p) = 1 - (1-p)^2` mapping |
| Boundary | `tests/boundary.rs`: `domain` never reaches `application`; no `wgpu|serde|web_sys|js_sys` in `src/`; `[dependencies]` empty |
| Determinism | `tests/simulation.rs`: same seed is bit-identical, different seed differs |
| Stability sweep | 500 ticks at parameter extremes stay finite and bounded |
| Edge darkening | wet-on-dry disc rims heavier than centre (asserted `> 1.15x`) |
| Wash centre | a staining pigment's dried disc keeps at least 60 % of its rim density at the centre, clearly more than a sedimentary pigment of the same density; a full-load disc dries without losing pigment to the deposit clamp |
| Wet-on-wet spread | pre-wetted area spreads 90 % pigment radius `> 1.3x` further |
| Pooling | the free-surface flux conserves water, and a dried disc's interior deposit correlates with the paper's valleys more than without it |
| Playback | `tests/playback.rs`: seek-vs-replay bit-equality, finish-immediately dryness, front-loaded elapsed-time advance, checkpoint bound |
| Mask raster | `tests/mask_raster.rs`: FNV-1a snapshot of `rasterize_mask_aspect` over 64 synthetic masks, captured from the original code and green on the accelerated one; degenerate polygons (fewer than three points) do not panic |

### `nocturne-watercolour-infra` (GPU needed; tests print and return early without an adapter)

Run this suite with `--release`: `reveal_preserves_the_artwork` aborts with a
native exit code partway through in debug builds on the reference machine (the
CPU-simulation tests are not feasible unoptimised) and passes in release
(7/7, ~7-14 min).

| Area | What is asserted |
|---|---|
| Document round trip | every catalogue scene round-trips through `scene_to_json`/`parse_scene_json`; newer/missing versions rejected before the body is parsed; `background` defaults to `"transparent"` when absent |
| GPU lockstep | `tests/gpu.rs`: CPU<->GPU mean absolute difference below **0.01** on both the finished image and the deposited-pigment field (128^2 sim); seeded replay bit-identical on the same device; 128 vs 1024 renders from one state agree in mean alpha; checkpoint seek equals straight replay (bit-identical readback); the checkpoint bound |
| Catalogue | `tests/catalogue.rs`: every id x palette x level x intensity validates; Small is simpler than Large; determinism; a CPU smoke render per id |
| Export | `PngExporter` unpremultiply, sRGB round-trip, valid PNG header |
| Scene tools (wasm crate) | every id builds on both surfaces with the same palette; unknown artwork/palette are typed; palette JSON documents accepted; surface/detail parsing; intensity monotone and in-bounds; strip stitching caps; manifest shape |
| Mask hashes | `tests/mask_hashes.rs`: every catalogue `SetMask` at every detail and both grounds, rasterised at the scene's own resolution and aspect, folded into one FNV-1a hash per detail against literals from the original code |
| Lucide icons | `tests/lucide.rs`: every test-set icon builds a valid scene for every detail and both grounds with the shipped hints; Small is simpler than Large; same inputs build identical scenes; `fill` moves exactly the named subpaths to bodies |

### Web (`@nocturne/watercolour`)

| Area | What is asserted |
|---|---|
| Scheduler | `scheduler.test.ts`: one rAF loop, visibility handling, frame clamping |
| Mode resolution | `mode.test.ts`: `resolveMode` matrix (auto/reduced/live/baked/static) and `fallbackOrder` |
| Baked manifest | `baked.test.ts`: `parseBakedManifest` caps (16 frames, 256 px), `stripFramePosition` mapping, load helpers |
| Assets | `assets.test.ts`: `defaultPaletteFor` stays in sync with `scripts/bake-manifest.json`; bundled lookup and fallback |
| Scene documents | `scenes.test.ts`: version-first rejection, `paletteKey`, icon sources through `iconScene` (element list serialisation, built-in hints merged under caller hints, empty-hints default, the plain-SVG fallback), the vanilla `lucide` `IconNode` assignability |
| Player static rung | `playback.test.ts`: `iconStaticBackend` routes a baked icon to its final and an unbaked one to the SVG backend |
| Components | `components.test.ts`: `detailForEdge` thresholds, `seedFromName` determinism/FNV-1a, `artworkOptionsFrom` defaults, `hostSurface` |

### Showcase (`@nocturne/watercolour-showcase`)

Vitest unit tests for the synthetic data (`units`, `history`) and the
`ShowcaseSettings` store (defaults, option forwarding, reset). `check`,
`test` and `build` run clean for the scaffold.

## What was verified in a browser

- **Stage 2 (wasm adapter + TS API) was verified live in Chrome 153**: WebGPU
  worked end to end, wasm module 778 KB / 277 KB gzip, warm GPU init 59-90 ms,
  steady frame ~0.2 ms.
- The progress log records the engineered visual outcomes (wash over light
  reads as watercolour after the refinement pass; Luminous accepted for dark
  hosts; aspect-aware accents re-checked). All visual review was done by the
  orchestrator, not by the worker agents.

## The reveal instruments

Two `nocturne-watercolour-infra` examples exist to check the reveal rather
than squint at it. Both are CPU-reference by default and take catalogue ids
and palettes:

```bash
cargo run -p nocturne-watercolour-infra --example settle_probe --release -- crescent-moon 20
cargo run -p nocturne-watercolour-infra --example reveal_timing --release -- <out_dir> crescent-moon moonlight 16
```

**`settle_probe`** (`[id] [every] [gpu]`) prints one row per sampled tick:
tick, total water, wet-cell count, suspended pigment, deposited pigment. Read
it against the tail: the water and wet-cell columns should fall to zero as the
sheet dries, and the suspended-to-deposited handover is where the visible
settling happens. A tail that "looks dead" is either out of water (nothing to
act on), pigment already deposited (nothing left in suspension), or flow that
stopped; the columns say which. A third argument of `gpu` runs the wgpu engine
and reads the grid back each sample, so the two ports can be compared across
the whole run rather than only at the finished frame.

**`reveal_timing`** (`<out_dir> [id] [palette] [frames]`) renders `frames`
evenly spaced in wall-clock progress: it seeks by progress, so it follows the
playback's `Reveal` curve and lands in the viewer's time (the paint phase in
the first ~600 ms, the tail over the rest) instead of the simulation's. It
writes `frame-00.png`..`frame-NN.png`, a `strip.png` contact sheet over a
light ground and `strip-dark.png` over a dark one (4-column grid, 256 px per
cell), and prints per frame the wall-clock millisecond, simulation tick, mean
absolute difference from the previous frame, covered area and alpha-weighted
centroid. The verdict block prints the covered area at 25/50/100 % of the
paint window (the pen should have most of the artwork down by the time the
paint phase ends), whether the tail stays alive (its minimum frame-to-frame
MAE, and whether it decays monotonically - a stall that then jumps is as wrong
as a dead tail), and `--reference <dir>` diffs the final frame against an
earlier run. Choreography flags (`--tip-scale`, `--paint-spread`,
`--bloom-trail`, `--settle-fraction`, `--settle-budget`, `--wet-sheen`, ...)
sweep parameters without rebuilding.

## Showcase browser pass

All twelve showcase routes were loaded in Chrome 153 at 1280 px with a clean
console apart from Chrome's `powerPreference` notice; row selection, filtering
to the empty state, tab switching, dark-mode toggling and exports were
exercised. Screenshots live in `.playwright-mcp/stage3/` (gitignored).

The visual review of those captures (the luminous review's Folder B) found:

- **Stair-stepped edges are gone** on every piece: the cubic B-spline fix
  confirmed in the browser (`index-after-resolution-2.png` has a smooth
  contour). The edge fix is the one confirmed improvement.
- **Light surfaces integrate well.** Reports, onboarding, invites-accept,
  confirmations and history-empty are the strongest - artwork reads as painted
  watercolour, is well placed and clear of text.
- **Dark mode is the weak case.** The index wash band reads murky/grey on the
  dark surface - the same grey-dirt defect as the luminous tuning review - and
  is the only piece that looks like a blob rather than translucent watercolour.
- Several small accents (dashboard title band, settings swatches, integration
  bands) are so small they barely register; not wrong, just low-impact.

Perf: the existing single-instance numbers below stand; the multi-instance
measurement (several artworks sharing one engine host) is pending.

## Lucide visual review

The eighteen Lucide-derived icons were rendered at 48 px and large, on light
and dark ground, and reviewed in three batches by a vision model with the
orchestrator adjudicating (PNGs in `scratchpad/out/lucide/`); the nine that
failed or needed tuning were re-rendered with per-icon hints
(`scratchpad/out/lucide-hinted/`).

| Verdict | Icons |
|---|---|
| Pass as generated | `calendar`, `bell`, `phone`, `heart` (review A); `scale`, `megaphone`, `server`, `rocket` (review C) |
| Pass large; small-size legibility needs tuning | `book-open`, `database` (review B) |
| Fixed by the hint pass | `clock`, `database`, `flag`, `sprout` (hinted re-review) |
| Partly fixed; small-size mark congestion remains | `cpu`, `fingerprint`, `battery`, `syringe`, `key` (hinted re-review) |
| Cannot work from a line icon | none. `key` is the closest; the hand key (round head, visible hole, two teeth) stays the reference |

The hint pass resolved every cleanly-structural failure (leaf fill, cutout
closure, pole attachment, ellipse weight, hand contrast) and left the remaining
problems as small-size mark congestion, which is tunable rather than structural.
Nothing regressed between the plain and hinted renders.

## Measured performance

### Native GPU (NVIDIA GeForce RTX 5060 Laptop GPU, wgpu 30.0.1 via Vulkan, Windows 11, release)

From `examples/render_native.rs`, seed 42, 256^2 sim, 4 pigments:

| Measurement | wash (320 ticks) | crescent_moon (360 ticks) | glaze_pair (400 ticks) |
|---|---|---|---|
| Device + pipeline init | 2.2-6.5 s (first run of the process; shader compilation) | - | - |
| GPU straight run, per tick (incl. stroke uploads and checkpoints) | 0.58 ms | 0.76 ms | 0.49-0.53 ms |
| GPU render 512^2 incl. readback | 68-91 ms | 80-84 ms | 78-83 ms |
| GPU 8 reveal frames 256^2 (seek + replay + render) | 163 ms | 244 ms | 243 ms |
| CPU reference, per tick | 28.6 ms | 21.5 ms | 20.4 ms |
| CPU render 512^2 | 166 ms | 94 ms | 120 ms |
| **CPU<->GPU finished frame, mean abs diff** (linear premultiplied RGBA, Subtractive) | 0.0010 (max 0.137) | 0.0003-0.0005 (max 0.067-0.093) | 0.0-0.00005 (max 0.005-0.103) |

Whole-catalogue render (every id, the three detail levels that existed when it was
measured, both grounds) took 8.7 s.
Small catalogue renders cost 20-50 ms on the GPU, Large 60-200 ms.

### Mask rasteriser

`rasterize_mask_aspect` went from `O(cells x points)` to `O(band area + cells)`
with two bit-identical culling passes: each segment is walked only over its
bounding box expanded by `feather + radius`, and the even-odd inside test is
pre-bucketed per row. Median of 5 runs, `--release`, on the RTX 5060 Laptop
(Vulkan):

| Case | Before | After | Speed-up |
|---|---:|---:|---:|
| `Path` mask, 16 points, 384 grid | 5.95 ms | 0.23 ms | 25.6x |
| `Path` mask, 96 points, 384 grid | 22.05 ms | 0.55 ms | 40.0x |
| `Path` mask, 400 points, 384 grid | 116.2 ms | 1.69 ms | 68.7x |
| `Polygon` mask, 96 points, 384 grid | 36.77 ms | 11.94 ms | 3.1x |
| Whole catalogue, every `SetMask`, `Large` | 378.2 ms | 166.7 ms | 2.3x |
| Whole catalogue, every `SetMask`, `ExtraLarge` | 792.2 ms | 382.0 ms | 2.1x |

The `Path` variant (the one the flattened-SVG feature feeds) is 20-110x
faster; `Polygon` 1.3-3.5x, bounded by the inside test. The final-gates run
re-measured the whole catalogue at 165.4 ms (`Large`) and 391.3 ms
(`ExtraLarge`), inside the mask worker's ranges. Bit-identity to the original
output is asserted by the FNV-1a snapshot tests named above, which go red under
a deliberately wrong band.

### Browser (Chrome 153, Windows 11, RTX 5060 Laptop GPU; 512^2 canvas, 256^2 sim grid, showcase defaults)

| Measurement | Value |
|---|---|
| wasm module | 652,953 B, 274,570 B gzip with `wasm-opt -Os` (binaryen 132); 966,642 B / 339,648 B gzip when the build machine lacks `wasm-opt` and the build script skips the pass. The `iconScene` surface added +163 KB raw / +56 KB gzip before optimisation. |
| GPU init (adapter + device + pipeline compile, once per page) | 59-90 ms warm; ~2.1 s cold (`engine.stats().initMs`) |
| First painted frame after a cold `createArtworkPlayer` | 1.1-1.6 s warm (from `performance.mark` around module load, engine init, first presented frame; the showcase exposes `watercolour:first-paint`) |
| Steady-state scheduler cost | ~0.2 ms per frame (CPU step + render submission; `getScheduler().stats()` histogram) |
| Checkpoints | ~44 MB per live instance in the browser (10 checkpoints at 256^2 x 4 pigments) |
| Baked assets | 5.4 MB on disk as WebP, from 20.3 MB as PNG (400 files: 100 sets x 4 files, of which the twelve `lucide-<name>` sets are 96) |

Methodology: `performance.mark`/`performance.now` around module load,
`WatercolourEngine.create`, first paint and each scheduler tick; single-run
spot checks on the machine above, not a benchmark harness.

## Limitations

- **Single alpha per pixel** (`ALPHA_SOFTNESS = 0.6`): a chromatic glaze loses a
  little saturation over white and glows brighter than physical over dark; a
  dense body pigment still reads as an opaque slab over dark (that is what the
  `for_dark_surface()` palettes are for).
- **CPU/GPU not bit-exact**: a few boundary cells dry one tick apart under
  fused-multiply-add rounding. Mean difference stays three orders of magnitude
  inside the 0.01 tolerance; see the measured table.
- **Luminous pale greens and yellows read slightly grey on dark**: bodies are
  translucent glazes whose alpha grows with presence, and a pale colour at
  partial alpha over near-black greys. The pale lift and chroma gain limit
  it; a thin moss wash still reads a little olive. Preview hook exists
  (`luminous_variants` in `render_native`), not shipped.
- **A luminous outline wobbles at the cell scale**: the paint mask rounds a
  binary deposit's staircase but cannot recover a sub-cell edge, and a
  one-cell mark keeps its full weight rather than being rounded. The grid is
  fixed per detail tier rather than raised, because the simulation is not
  scale invariant; see "Resolution" in `pigment-and-compositing.md`.
- **The 600 ms reveal at a 512 grid can exceed the 16 ms frame budget** on the
  largest heroes: at `extraLarge` complexity (tick scale 1.25) the reveal needs
  ~18 ticks/frame at 60 fps, and at 512 each tick costs ~0.8-1.4 ms on the GPU
  (measured, per-tick numbers in the resolution report), so a large hero is
  ~14-25 ms/frame. The scheduler's catch-up keeps it correct, but not a smooth
  60 fps at the biggest backing sizes. Only an explicit `simResolution` takes
  a reveal there; the largest tier's own grid is 384.
- **Baked assets stay at their baked detail**; the resolution override applies
  to live mode only.
- **The host app's `svelte-check` has 46 pre-existing errors** unrelated to this
  library (stale generated NSwag client, bot api-client, two test/route files);
  the watercolour integration adds none.
- **Worker agents could not view images**, so visual verdicts came from the
  coordinator and the vision review, not from the build agents.
- **Checkpoint buffers live on the GPU only**; no host-side spill.
- **`GpuEngine::apply` submits one command buffer per operation and `render`
  blocks on readback** - fine for authoring and export; an interactive host
  would want to batch.
- **Device init is slow** (4-6 s native, ~2.1 s cold in the browser) because all
  shaders compile at engine creation; a pipeline cache is a later optimisation.
- **Baked strip cross-fade is not a true linear blend**; at 10-12 frames the
  difference is not visible.
- **`quality` is accepted but inert**; `offscreenCanvas` is probed but not used.
- **No texture-based rendering path**; output is buffer-based (no 256-byte row
  alignment concerns), which is fine for these sizes.
- **Performance numbers are single-run spot checks**, not a harness.