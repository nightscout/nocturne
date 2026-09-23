# nocturne-watercolour-core

See [docs/watercolour/](../../docs/watercolour/README.md) for the full developer documentation of the watercolour library.

Platform-independent half of the watercolour graphics engine: the domain model,
the CPU reference simulation (a simplified Curtis et al. 1997 step), Kubelka-Munk
optics, and the application ports and use cases that drive any backend. The crate
has **no dependencies beyond `std`**; `tests/boundary.rs` fails the build if a
`use wgpu|serde|web_sys|js_sys` appears anywhere in `src/`, if the domain reaches
into the application layer, or if `[dependencies]` is non-empty.

## Boundary

- `domain` is pure data and pure functions: pigments, palettes, paper, operations,
  timeline, scene validation, the simulation grid, the reference `sim` step, the
  stamp rasteriser and the optics/renderer. Nothing performs I/O or knows about a
  GPU, a serialisation format or wall-clock time.
- `application` holds the ports a backend implements (`Simulator`, `Renderer`,
  `Exporter`), the reference `CpuEngine` that implements the first two with the
  domain code, the `Playback` controller, the command-style use cases and the
  `Reveal` timeline builder. Callers supply elapsed time; nothing here schedules.

## Layout

| Module | Contents |
|---|---|
| `domain::seed` | `Seed(u64)`, `SubSeed` purposes, splitmix64 stream and the shared 2-D lattice hash |
| `domain::pigment` | `Pigment { k, s, density, staining_power, granulation }`, `Pigment::from_reflectance(Rw, Rb)` (Curtis derivation with documented clamps), 12 built-ins |
| `domain::palette` | `Palette` with `PigmentRole`s (base wash, shadow, accent, glow); named palettes `moonlight`, `water`, `dusk`, `ember`, `moss`, `slate`, each with a `for_dark_surface()` variant (`<name>_dark`); `MAX_PIGMENTS = 8` |
| `domain::paper` | `Paper` (seed, grain scale, height amplitude, absorbency range, fibre anisotropy) and `PaperField::generate(paper, w, h)` — four octaves of fine grain, two low-frequency pooling octaves and a fibre term, defined in normalised coordinates so every resolution samples the same paper; the render-resolution field is band-limited at the output pixel scale (`generate_with_pixel_scale`) so grain never reads as per-pixel speckle |
| `domain::ops` | `BrushStroke`, `WaterStroke`, `LiftStroke`, `Mask` (polygon or path + feather), `Operation` enum incl. `Dry { rate }`, `DryAll`, `SetMask`, `ClearMask`; geometry in `0..1` scene coordinates |
| `domain::timeline` | `TimelineEvent { at_tick, op }`, `Timeline { events, total_ticks }` |
| `domain::scene` | `Scene`, `Scene::validate() -> Result<(), Vec<ValidationError>>` (all errors, typed), `Scene::aspect`, `isotropic_scale` |
| `domain::grid` | `SimulationGrid`: wet mask, velocity, pressure/water depth, suspended and deposited pigment per pigment, capillary saturation and capacity, paper height, bleed mask, dry rate |
| `domain::paint` | Stamp rasterisation shared by every backend (paper-driven edge break-up, seeded radius jitter), mask rasterisation, `apply_brush/water/lift` |
| `domain::sim` | The reference step as `pass_*` functions (one per shader entry point), `SimParams`, `apply`, `dry_all` |
| `domain::optics` | Kubelka-Munk layer `(R, T)`, mixed layer, layered composite, premultiplied-alpha conversion, `render` |
| `domain::image` | `Image` (premultiplied linear RGBA `f32`), `composite_over`, `mean_abs_diff` |
| `application::ports` | `Simulator`, `Renderer`, `Exporter`, `CheckpointId`, `EngineError` |
| `application::cpu` | `CpuEngine`: the ports implemented with `domain::sim` and `domain::optics`; checkpoints are grid clones under a 256 MB budget |
| `application::playback` | `Playback<S: Simulator>`: play/pause/reset/seek/finish, artistic duration mapping, checkpoint policy |
| `application::use_cases` | `CreateScene`, `UpdateScene`, `ApplyOperation`, `Advance`, `AdvanceByElapsed`, `ExportFinished` |
| `application::reveal` | `Reveal` builder: mask, wash at tick 0, wet-into-wet drop-ins, wet-on-dry `glaze` phase (dries everything first), settle, implicit `DryAll` at the end |

## Pigment model and compositing

Each pigment carries Kubelka-Munk absorption `K` and scattering `S` per RGB channel,
derived from a designed reflectance over white `Rw` and over black `Rb` with Curtis's
formulas (`a = ½(Rw + (Rb − Rw + 1)/Rb)`, `b = √(a² − 1)`,
`S = arccoth((b² − (a − Rw)(a − 1)) / (b(1 − Rw))) / b`, `K = S(a − 1)`). Inputs are
clamped to `0.002 ≤ Rb ≤ Rw − 0.002` and `Rw ≤ 0.98` to avoid the singularities at
`Rb = 0`, `Rw = 1` and `Rb ≥ Rw`. A layer of thickness 1 over white reproduces `Rw`
(tested). The three Curtis behaviour coefficients are `density` (ρ, settling),
`staining_power` (ω, resistance to lifting) and `granulation` (γ, how strongly paper
height modulates deposition).

Every pixel is rendered as one mixed layer: `Kx = Σ t_k K_k`, `Sx = Σ t_k S_k` where
`t_k` is the pigment's optical thickness: its share of the pixel's total amount
(deposited plus a fixed share of suspended pigment) mapped through a saturating curve,
then modulated at output resolution by paper height for granulating pigments. A
Saunderson surface correction deepens heavy paint and the water film enriches the
colour while wet; see `docs/watercolour/pigment-and-compositing.md`. With `β = √(Kx² + 2 Kx Sx)`:
`R = Sx·sinh(β)/β / (a/b·sinh β + cosh β)`, `T = 1 / (a/b·sinh β + cosh β)`; the
formulation stays finite as `Sx → 0` and `β` is capped at 40. Layers stack with
Curtis's two-layer formula (`composite`).

**Transparent output.** Standard "over" wants one alpha, but a glaze's colour lives in
per-channel transmittance. The exported pixel is

```
return_c = T_c² / (1 − R_c)        (light that goes down, reflects off white, returns)
alpha    = 1 − lerp(min_c(return_c), mean_c(return_c), ALPHA_SOFTNESS)
rgb_c    = max(0, (R_c + return_c) − (1 − alpha))     premultiplied
```

With `ALPHA_SOFTNESS = 0` this is exact over white for every channel. The softness
(`0.6`) pulls alpha toward the mean transmittance so a medium wash stays translucent
over a dark ground instead of saturating as soon as one channel is absorbed; the cost,
bounded per channel by `ALPHA_SOFTNESS · (mean − min)` and asserted in the tests, is a
slight loss of saturation in strongly chromatic glazes over white. Other limitations,
all documented in `optics`: over black the transmitting channels glow brighter than the
physical `R_c` (worst channel error at thickness 0.15 asserted `< 0.3`); over mid tones
the interreflection term `1/(1 − R·B)` is not background-aware. This is an
approximation chosen so the output reads as watercolour on light grounds and as
translucent glow on dark ones in any compositor, not a claim of physical accuracy.
Output is linear RGB; sRGB encoding is the exporter's job.

**Composite modes.** The conversion above is `CompositeMode::Subtractive`. Under it a
pale glaze over a dark ground cannot glow (a thin yellow returns almost no light of
its own), so dark hosts use `CompositeMode::Luminous`, a display choice rather than
physics: the paint is shown as coloured light, a glaze whose alpha grows with the
pixel's dilated presence and carries the paper's tooth, coloured by the mix's on-white
colour pushed toward its hue, with pale colours laid more opaque so partial alpha does
not read as grey. The outline comes from a separate paint mask, so it is smooth at the
cell scale, and `rgb ≤ alpha` always holds. Pair it with the normal palettes. The
mode is carried by `Scene::background` (`Transparent` → Subtractive,
`TransparentOnDark` → Luminous), read through `Scene::composite_mode()`, stored in
`SimulationGrid::composite_mode` (so checkpoints and every backend read it from the same
state) and mirrored in `render.wgsl` through the state header. The model, its
constants' rationale and its tests are documented in
`docs/watercolour/pigment-and-compositing.md` and the `optics` module doc; the constants
are one value, `LUMINOUS_TUNING`, so `render_with_luminous_tuning` can render
alternatives.

**Dark-surface palettes.** `Pigment::luminous` (and `Palette::for_dark_surface`) makes
a pigment more transparent (`Rb × 0.3`), paler and more chromatic (`Rw` pushed away
from its mean and lifted toward 1) and thinner (`K`, `S × 0.5`). Under Subtractive
compositing this is what lets a glaze glow at all (transmitted hue light); under
Luminous it keeps the glaze thin and airy. Neutrals (Payne's grey, lamp black) glow
little by construction.

## Paper

`sample_height(u, v)` sums four octaves of hash-based value noise (fine grain, from
`grain_scale` cells across the sheet, ×2.3 per octave), two low-frequency pooling
octaves at `0.1×` and `0.05×` the grain frequency weighted `POOL_WEIGHT = 0.6` against
the grain, and a fibre term stretched along `x`, all scaled by `height_amplitude` around
`0.5`. Capillary capacity follows the same height. The pooling octaves are what give a
wash its broad cloudy patches: water flows downhill into them (`slope_gain`), the
stroke itself lays more water in them (`paint::STROKE_WATER_PAPER_GAIN`), and pigment
is carried with the water. Granulating pigments additionally read the full field at
output resolution (fine grain + fibre streaks) through `RenderParams::granulation_gain`;
non-granulating pigments stay smooth apart from the pooling they inherit from the
water. Because that render field is sampled at output resolution, on a large canvas the
finest grain octaves fall at one to two output pixels and modulate every pixel
independently, which reads as per-pixel speckle. The render paper is therefore
band-limited by `octave_band(pixel_scale, min_period_px, full_period_px, freq)` with the
window from `grain_band_window(long_edge)` — `min = max(2, long_edge/256)`, `full = 1.5·min`
— so each octave and the fibre keep full weight while their period in output pixels is at
least `full_period_px` and fade out below `min_period_px`, a Hermite smoothstep on log
period. 256 and 512 outputs keep the base 2/3 px window; the window grows with the canvas
(768 gets 3/4.5 px, 1024 gets 4/6 px) so a large output drops the octaves that fall at a
few pixels while a small canvas keeps its base tooth. `pixel_scale` is one
output pixel in the scene's isotropic metric (`render_pixel_scale(width, height, aspect)`),
identical on both backends. The simulation paper (`generate_with_aspect`) keeps full
octaves — its cell size is the sim cell — so the fluid rules are unchanged.

**Non-square outputs.** The simulation grid is square and is stretched to
`Scene::size_hint`. So that grain, pooling octaves, stamps and feathers stay round after
the stretch, distances are measured in an isotropic metric (`scene::isotropic_scale`):
the shorter side spans `0..1`, the longer `0..max(aspect, 1/aspect)`. `PaperField::
generate_with_aspect` samples the noise there (`generate` is the aspect-1 wrapper);
`paint::rasterize_path_aspect` / `rasterize_mask_aspect` measure radius, edge shift,
anti-aliasing and feather there while positions stay normalised; `SimulationGrid::aspect`
(from the paper field, packed in the state header) is what `sim::apply` uses. Not
aspect-aware, by design of the square grid: the fluid step itself, whose per-cell
diffusion, advection and blur distances are still stretched with the grid. Tests: a
radius-0.1 disc on a 4:1 scene spans equal extents in scene units on both axes (within
15 %); the 4:1 paper's autocorrelation lengths in pixels agree on both axes within 25 %
while the square-metric field's differ by more than 2.5×.

## Numerical limits (`domain::sim`)

- `DT = 1` per tick. Velocities clamp to `max_velocity = 0.45` cells/tick so
  `|u| + |v| < 1` and upwind advection never drains a cell.
- Viscosity (`0.1`) and the diffusion coefficients (`pigment 0.05`, `water 0.1`,
  divided by four per neighbour) are explicit Laplacian weights; keep `viscosity ≤ 0.25`
  and diffusion `≤ 1.0`.
- Velocity gains are `slope_gain = 1.6` (paper) and `pressure_gain = 0.9` (depth), so
  a still puddle's depth varies as `1.8×` the paper height (pooling); `drag = 0.02`.
- Divergence relaxation is a fixed 8 Jacobi iterations, not a tolerance, so CPU and GPU
  do identical work.
- The flow-outward drain at the wet boundary is
  `eta · (1 − M_blurred) · clamp(p / DRAIN_DEPTH, DRAIN_MIN, DRAIN_MAX) · (1.5 − h)`
  with `eta = 0.06`, `DRAIN_DEPTH = 0.5`, clamp `[0.15, 2.0]`: water that pooled in a
  low spot at the edge drains harder than a thin film on a high spot, which is what
  varies the dried rim's weight along the boundary.
- Stroke water is `water · coverage · (1 + 0.5 · (0.5 − h) · 2)` (never negative).
- Deposited pigment, suspended pigment and water depth are clamped to their
  `sim` maxima (`MAX_DEPOSITED`, `MAX_SUSPENDED`, `MAX_WATER_DEPTH`) after each pass.
- Deviations from Curtis (collocated velocities, advected water depth, depth-weighted
  pigment diffusion, no capillary destination threshold, depth- and height-scaled edge
  drain, paper-modulated stroke water) are listed in the module doc. The stability
  sweep test runs 500 ticks at parameter extremes and asserts finiteness and bounds.

Measured behaviour on the reference: a wet-on-dry disc ends with deposited pigment
heavier in the rim band than at the centre (edge darkening, asserted `> 1.15×`); the
same brush into a pre-wetted area spreads its 90 % pigment radius `> 1.3×` further.

## Playback, seeking and the checkpoint bound

Tick `t` means `t` steps have run; events at the current tick apply before the step,
events at `total_ticks` apply at the end, then an implicit `DryAll`. `duration_ms` maps
progress `p` to ticks through `ease(p) = 1 − (1 − p)²`, so half the duration runs three
quarters of the ticks (wash lands fast, then settles). Seeking restores the nearest
checkpoint at or before the target and replays; it never edits a timestamp, and
`seek(t)` + `step` is bit-identical to a straight run (tested on the CPU engine).

Checkpoints are taken at tick 0, at every event tick and every `every_ticks` (32);
when the backend's capacity is full the oldest periodic non-event checkpoint is
released, and if none remain no more are taken. Capacity is
`clamp(budget / checkpoint_bytes, 1, 64)` with a 256 MB budget, where a checkpoint is
`(10 + 2·pigments) · cells · 4` bytes: 25 MB at 512² with 8 pigments (10 checkpoints),
4.7 MB at 256² with 4 pigments (54).

## Tests

```bash
cd crates
cargo test -p nocturne-watercolour-core
```

Unit tests cover validation, seeding, paper, palettes, stamps, KM (zero thickness is
clear, unit thickness recovers `Rw`, alpha conversion exact over white and bounded
over black, two glazes darker than one) and the easing. Integration tests
(`tests/`) cover the layer boundary, seed determinism (bit-identical grids), the
stability sweep, edge darkening, wet-on-wet spread, seek-vs-replay equality,
finish-immediately dryness, front-loaded elapsed-time advance and the checkpoint bound.

## Native integration points

Android (or any host) consumes this crate unchanged and supplies a backend. A native
adapter implements:

- `Simulator`: `load` (allocate state for `sim_resolution` and the palette),
  `apply` (rasterise with `domain::paint`, upload, add), `tick`/`step`, `snapshot`/
  `restore`/`release` with a stated capacity;
- `Renderer`: resample the sim grid to any output size and run the `optics` rules
  (the WGSL in `nocturne-watercolour-infra` is a direct port to copy from);
- `Exporter` if the host needs encoded bytes; otherwise consume `Image` directly;
- a clock: call `Playback::advance_by_elapsed` from the host's frame callback.

`nocturne-watercolour-infra` is the wgpu implementation of exactly that list.
