# nocturne-watercolour-infra

See [docs/watercolour/](../../docs/watercolour/README.md) for the full developer documentation of the watercolour library.

Infrastructure for the watercolour engine: a wgpu 30 / WGSL implementation of the
`Simulator` and `Renderer` ports, versioned serde scene documents, PNG export, a
reveal frame-sequence helper and the artwork catalogue. Everything platform-specific
lives here so `nocturne-watercolour-core` stays `std`-only.

## Layout

| Module | Contents |
|---|---|
| `gpu` | `GpuContext` (adapter/device/queue; `try_new` returns `None` without an adapter), `GpuEngine` implementing `Simulator + Renderer`, `StateLayout` (packed buffer offsets), `shaders/*.wgsl` |
| `document` | `SceneDocumentV1` (`version: 1`), explicit `to_document` / `from_document` mappers, `parse_scene_json` (reads `version` first, rejects unknown versions with `DocumentError::UnsupportedVersion`), `scene_to_json` |
| `export` | `PngExporter` (straight-alpha sRGB 8-bit, careful un-premultiply), `linear_to_srgb`/`srgb_to_linear`, `FrameSequence` (K frames evenly spaced in artistic progress, last frame finished and dry) |
| `authoring` | `ArtworkCatalogue`: the fifteen web artworks by kebab-case id (`by_id`, `by_id_for`, `IDS`), `DetailLevel`, the `Painting` phase builder and the design-space geometry (`Frame`, `Crescent`, `Hills`, `WaterBody`); plus the original `wash`, `crescent_moon`, `glaze_pair` `(Seed, Palette) -> Scene` entries. See "Artwork catalogue" below |
| `examples/render_catalogue.rs` | Renders every catalogue id at the four detail levels over a light and a dark ground (normal palette, `Background::TransparentOnDark` for the dark set) and writes per-level contact sheets; prints ticks and timings |
| `examples/render_native.rs` | Renders the catalogue on the GPU in each artwork's normal and `for_dark_surface()` palette, composites every result over a light (#f7f5f0) and a dark (#0f1420) ground, writes 8 reveal frames, renders the normal variant on the CPU reference and prints the difference and timings |

## GPU design

All per-cell fields are packed into one `array<f32>` **state** buffer and one
**scratch** buffer (`StateLayout` / `common.wgsl`), which keeps the port inside
WebGPU's default eight storage buffers per stage and makes a checkpoint a single
`copy_buffer_to_buffer`. Each pass reads `state` and writes its `scratch` region; the
host copies the region back where the CPU reference swaps vectors, so no pass ever
reads its own output. Stroke geometry is rasterised on the CPU by the shared
`domain::paint` code and uploaded as a coverage field, so both backends see identical
stamps and masks. Rendering is a compute pass into an `array<vec4<f32>>` of the
requested output size (cubic B-spline reconstruction of the sim grid, paper height generated at
output resolution — band-limited by the core to drop grain octaves finer than a few
pixels — and uploaded), copied to a staging buffer and mapped; buffers rather
than textures, so no 256-byte row alignment is involved. Output is premultiplied linear
RGBA; `PngExporter` un-premultiplies (pixels with `alpha < 1/1024` are written as
transparent black) and encodes sRGB.

The composite mode (`optics::CompositeMode`) travels in the state header: `StateLayout::pack`
writes `grid.composite_mode.flag()` into the spare float after `dry_rate`, `render.wgsl`
reads it, and checkpoints carry it for free. The engine's `load` must build its grid with
`SimulationGrid::new(..).with_composite_mode(scene.composite_mode())` for the flag to reach
the GPU; until it does, the GPU renders subtractively and the luminous lockstep test and
the example's dark variants report that and fall back (test skips, example uses the CPU).

Non-square scenes: the core measures paper grain, stamps and feathers in the isotropic
metric of `Scene::aspect()` (see the core README). The GPU engine gets that for free
once its three core calls take the aspect: `PaperField::generate_with_aspect(&scene.paper,
res, res, scene.aspect())` in `load`, `PaperField::generate_with_pixel_scale(&paper, width,
height, aspect, render_pixel_scale(width, height, aspect))` for the render paper (the
pixel scale sizes one output pixel; the grain band window `grain_band_window(max(w,h))`
grows with the long edge, so large outputs drop fine octaves), and `paint::rasterize_path_aspect` /
`rasterize_mask_aspect` with the same aspect where strokes and masks are rasterised.
Until then the GPU renders non-square scenes in the square metric (stretched grain and
stamps); stamps are CPU-rasterised on both backends, so no shader changes are involved.

Checkpoints are GPU buffer copies under a 256 MB budget
(`min(budget / state_bytes, 64)`: 10 at 512² × 8 pigments, 54 at 256² × 4, none
below one);
`Playback` falls back to reload-and-replay from tick 0 when no checkpoint precedes the
target. Ticks are encoded in batches of up to 16 per command buffer.

### Which rules run in shaders

| Shader | Entry points | Rule (CPU reference) | Deviation from CPU reference |
|---|---|---|---|
| `velocity.wgsl` | `velocity` | Curtis UpdateVelocities (`sim::pass_velocity`): slope + depth gradient, viscosity, drag, clamp, dry-neighbour zeroing | none |
| `pressure.wgsl` | `divergence`, `jacobi_a`, `jacobi_b`, `project` | Curtis RelaxDivergence (`pass_divergence`, `pass_jacobi` × 8 ping-pong, `pass_project`) | none (host copies `q2 → q` when the iteration count is odd) |
| `flow.wgsl` | `blur_h`, `blur_v`, `advect` | Curtis FlowOutward + MovePigment (`pass_blur_h/v`, `pass_advect`): box blur of the wet mask, upwind advection of water and pigment, depth-weighted diffusion, edge drain scaled by local depth (`clamp(p / DRAIN_DEPTH, 0.15, 2)`) and paper height (`1.5 − h`) | none |
| `transfer.wgsl` | `transfer` | Curtis TransferPigment + evaporation, capillary absorption, drying (`pass_transfer`) | none |
| `capillary.wgsl` | `capillary`, `capillary_wet` | Curtis SimulateCapillaryFlow (`pass_capillary`): symmetric pair transfer into `s2`, then bloom wetting | none |
| `apply.wgsl` | `apply_brush`, `apply_water`, `apply_lift`, `dry_all` | `paint::apply_*`, `sim::dry_all` on an uploaded stamp; stroke water scaled by `paint::stroke_water_factor(h)` | none |
| `render.wgsl` | `render` | `optics::render`: cubic B-spline reconstruction (16 taps, ~4× the cell reads of bilinear; see `optics`' module doc), granulation, mixed KM layer, premultiplied conversion with `ALPHA_SOFTNESS` in the `CompositeMode` read from the state header (`StateLayout::composite_mode`, the float after `dry_rate`) | f32 transcendental precision only |

Shared constants (`DRAIN_DEPTH`, `DRAIN_MIN`, `DRAIN_MAX`, `STROKE_WATER_PAPER_GAIN` in
`common.wgsl`; `ALPHA_SOFTNESS` in `render.wgsl`) mirror the `pub const`s in
`domain::sim`, `domain::paint` and `domain::optics`; tunable parameters travel in the
`Params` uniform.

Deviations from Curtis are those of the CPU reference and are listed in
`nocturne_watercolour_core::domain::sim`.

## Measured on this machine

NVIDIA GeForce RTX 5060 Laptop GPU, wgpu 30.0.1 picked **Vulkan**
(`InstanceDescriptor::new_without_display_handle_from_env`, high-performance
preference). Windows 11, release build, `examples/render_native.rs`, seed 42,
256×256 simulation, 4 pigments:

| Measurement | wash (320 ticks) | crescent_moon (360 ticks) | glaze_pair (400 ticks) |
|---|---|---|---|
| Device + pipeline init | 2.2–6.5 s (first run of the process; includes shader compilation) | — | — |
| GPU straight run, per tick (incl. stroke uploads and checkpoints) | 0.58 ms | 0.76 ms | 0.49–0.53 ms |
| GPU render 512×512 incl. readback | 68–91 ms | 80–84 ms | 78–83 ms |
| GPU 8 reveal frames 256×256 (seek + replay + render) | 163 ms | 244 ms | 243 ms |
| CPU reference, per tick | 28.6 ms | 21.5 ms | 20.4 ms |
| CPU render 512×512 | 166 ms | 94 ms | 120 ms |
| **CPU ↔ GPU finished frame, mean abs diff** (linear premultiplied RGBA, Subtractive) | **0.0010** (max 0.137) | **0.0003–0.0005** (max 0.067–0.093) | **0.0–0.00005** (max 0.005–0.103) |

`tests/gpu.rs` asserts the CPU↔GPU mean absolute difference below **0.01** on both the
finished image and the deposited-pigment field (128² sim). Stage 1 measured ~0 because
both sides ran the same f32 expression order; after the visual-refinement pass (faster,
less damped flow, depth-scaled drain) a handful of boundary cells dry one tick apart
under fused-multiply-add rounding, which shows up as isolated max differences of
0.07–0.14 while the mean stays at or below 1e-3, a factor of ten inside the tolerance.
The same test file checks seeded replay is bit-identical on the same device, that 128
and 1024 renders from one state agree in mean alpha, that checkpoint seek equals
straight replay (bit-identical grid readback), and the checkpoint bound. Every GPU test
prints and returns early when no hardware device opens; a software rasteriser (llvmpipe,
WARP) counts as none.

## Running

```bash
cd crates
cargo run -p nocturne-watercolour-infra --example render_native --release -- <out_dir>
cargo test -p nocturne-watercolour-infra
cargo test -p nocturne-watercolour-infra --release --test reveal_preserves_the_artwork -- --ignored
```

The default run checks the reveal gates in `tests/reveal_preserves_the_artwork.rs` on one
artwork at `Medium` detail. The catalogue-wide sweeps at `Large` are `#[ignore]`d: they
take about an hour in `--release` on 16 cores. Run them after changing the catalogue,
the choreography or the simulation. CI runs them weekly and on demand
(`.github/workflows/watercolour-reveal-sweep.yml`); the per-change `watercolour` job in
`tests.yml` runs the default suite.

The example writes, per artwork and per palette variant (`normal`, `dark`):
`<name>_<variant>_gpu.png` (transparent, 512²), `<name>_<variant>_gpu_over_light.png`
(#f7f5f0) and `<name>_<variant>_gpu_over_dark.png` (#0f1420); and for the normal
variant `<name>_cpu.png` (CPU reference, 512²) and `<name>_reveal_00..07.png` (256²).

## Artwork catalogue

```rust
ArtworkCatalogue::by_id(id, seed, &palette, intensity, detail)                 // light ground
ArtworkCatalogue::by_id_for(id, seed, &palette, intensity, detail, background) // any ground
```

Ids (they match the TypeScript union): hero icons `crescent-moon`, `alarm-bell`,
`linked-rings`, `report-pages`, `magnifying-glass`, `confirmation-mark`; scenes
`moonlit-shoreline` (16:9), `distant-mountains`, `connected-shores` (2:1),
`overlapping-shapes`; accents `avatar-wash`, `tab-underline` (8:1), `selection-edge`
(1:6), `confirmation-background` (3:1), `header-motif` (5:1). Unknown ids return `None`.
Every artwork derives all randomness from the seed, addresses pigments by
`PigmentRole` so any palette works, and paints nothing outside its shapes.

**Detail levels.** `DetailLevel::Small` (32-48 px) keeps one bold silhouette per
artwork; `Medium` (96-192 px) adds the secondary marks (halos, blooms, line hints,
drop-ins); `Large` adds the tertiary ones; `ExtraLarge` (320 px and up, e.g. a 16:9
hero) keeps the Large marks on a 384² grid. The simulation grid follows: 96² / 160² /
256² / 384², with tick counts scaled 0.5 / 0.75 / 1 / 1.25 because water covers more
of the sheet per tick on a coarser grid. A live caller can override the grid up to
`SimResolution::MAX` (512) via `by_id_for_with_resolution`. Small renders cost
20-50 ms on the GPU, Large 60-200 ms.

**Intensity.** `0..1`, `0.7` is the designed look. Concentration scales by
`0.4 + 0.857 i` (0.3 keeps two thirds, 1.0 adds a quarter) and water by
`0.8 + 0.3 i`, so a higher value also pools and rims harder; both stay inside the
scene bounds so 1.0 never clips to mud.

**Ground awareness.** `by_id_for` stores `background` on the scene and consults it
while authoring: under `Background::TransparentOnDark` (luminous compositing) the
moons are pure glow with no shadow whisper inside the body, the magnifying glass has
no lens tint, and `confirmation-background`'s second sweep is a whisper of the base
pigment rather than a glow glaze, because those marks grey or slab under the luminous
conversion. Use the **normal** palette on both grounds; `for_dark_surface()` palettes
are for subtractive compositing on dark only.

**Geometry.** The simulation grid is square and stretched to `size_hint` on output, so
the non-square artworks are authored in a design space (`y` in `0..1`, `x` in
`0..aspect`) through `Frame`, which maps shapes to normalised coordinates. Stamp
radii, mask feathers and paper grain are measured by the core in the same isotropic
metric (`scene::isotropic_scale`: shorter side `0..1`, longer side `0..aspect`), so a
radius written in design units comes out round and the grain stays isotropic on every
aspect.

```bash
cargo run -p nocturne-watercolour-infra --example render_catalogue --release -- <out_dir> [id] [palette]
```

writes `<id>_<small|medium|large|extralarge>_<light|dark>.png` (Small upscaled ×4 nearest for
legibility checks) and `sheet_<level>_<ground>.png` contact sheets.
`tests/catalogue.rs` checks every id × palette × level × intensity validates, that
Small is simpler than Large, determinism, and a CPU smoke render per id.

## Scene documents

`SceneDocumentV1` mirrors the domain types field for field with plain serde structs
(externally tagged operations and masks such as `{"brush": {...}}`, string pigment
roles, `background` as `"transparent"` or `"transparent_on_dark"` with a default of
`"transparent"` so documents written before the field existed still parse). Domain
types stay serde-free; the mappers are explicit so a schema change is a deliberate new
version. The enums are externally rather than internally tagged because
another workspace crate enables `serde_json/arbitrary_precision`, under which
internally tagged enums fail to parse floats (see the comment on `OperationDoc`). `parse_scene_json` reads `version` before anything else: a newer or missing
version is a typed error, not a field error deep in the body. Round-trip, version and
validation-failure tests are in `document.rs`.

## Limitations and shortcuts

- Single alpha per pixel (see the core README): with `ALPHA_SOFTNESS = 0.6` a chromatic
  glaze loses a little saturation over white and glows brighter than physical over
  dark. A dense body pigment such as cerulean in the normal palette still reads as a
  fairly opaque slab over a dark ground; that is what the `for_dark_surface()` palettes
  are for.
- The CPU↔GPU difference is no longer bit-exact: the faster, less damped flow makes
  drying decisions sensitive to fused-multiply-add rounding, so a few cells dry a tick
  apart on the two backends. The mean stays three orders of magnitude inside the
  tolerance; see the measured table.
- Checkpoint buffers live on the GPU only; there is no host-side spill.
- `GpuEngine::apply` submits one command buffer per operation and `render` blocks on
  readback. Fine for authoring and export; an interactive host would want to batch.
- Device init is slow on this machine (4–6 s) because all shaders compile at engine
  creation; a pipeline cache is a later optimisation.
- No wasm or JS bindings and no texture-based path; both are stage 2.

## What a native adapter would implement

An Android host would replace this crate with a wgpu (or Vulkan) adapter of the same
shape: allocate the packed state/scratch buffers from `StateLayout`, port the seven
`.wgsl` files (they are plain WGSL with no extensions), upload stamps from
`domain::paint`, implement `snapshot`/`restore` as buffer copies with a stated budget,
implement `render` into a texture or buffer at the view's size, and drive
`Playback::advance_by_elapsed` from the frame clock. The core crate needs no changes.
