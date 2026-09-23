# Architecture

The engine follows Clean Architecture. The dependency direction is strictly
inward: domain and application live in `nocturne-watercolour-core` with **zero
dependencies**, infrastructure (wgpu/WGSL, serde documents, export, catalogue)
depends on core, and the wasm adapter depends on infra. On the web side the
wasm crate is the adapter and the TypeScript API plus Svelte components plus
the showcase are the presentation layer.

```
domain (pure data + functions)
   ^
application (ports, CpuEngine, Playback, use cases)   <- nocturne-watercolour-core (std only)
   ^
infrastructure (GpuEngine/WGSL, scene documents, export, catalogue)   <- nocturne-watercolour-infra
   ^
web adapter (wasm-bindgen)                            <- nocturne-watercolour-wasm
   ^
presentation (TS API, Svelte components, showcase)    <- @nocturne/watercolour + showcase
```

## Layers

| Layer | Location | Contents |
|---|---|---|
| Domain | `core/src/domain/` | Pigments, palettes, paper, operations, timeline, scene validation, the simulation grid, the reference simulation step, stamp rasterisation, Kubelka-Munk optics, the `Image` type. Pure data and pure functions; no I/O, no GPU, no serialisation, no wall clock. |
| Application | `core/src/application/` | The ports a backend implements, the reference `CpuEngine` (ports implemented with domain code), the `Playback` controller, command-style use cases, the `Reveal` timeline builder, and the `choreography` timeline transform. Callers supply elapsed time; nothing here schedules. |
| Infrastructure | `infra/src/` | `gpu` (wgpu/WGSL `Simulator` + `Renderer`), `document` (serde `SceneDocumentV1`), `export` (PNG, frame sequences), `authoring` (artwork catalogue, Lucide icon authoring in `svg.rs`). Everything platform-specific lives here so core stays `std`-only. |
| Web adapter | `wasm/src/` | `web.rs`: the wasm-bindgen surface (one shared `WatercolourEngine`, per-scene `SceneInstance`); `scene_tools.rs`: platform-neutral scene construction, intensity, strip stitching, the baked manifest. |
| Presentation | `src/Web/packages/watercolour/` | `src/api/*.ts` (player, capabilities, engine host, scheduler, mode resolution, baked/static assets, scene documents), `src/components/*.svelte`, baked assets in `assets/`, wasm output in `src/wasm/`. The showcase app (`watercolour-showcase`) renders the components across its routes. |

## Ports and use cases

The three ports (`core/src/application/ports.rs`) are what every backend
implements:

| Port | Responsibility | Reference impl | GPU impl |
|---|---|---|---|
| `Simulator` | `load` a scene, `apply` operations, `step`/`tick` the grid, `snapshot`/`restore`/`release` checkpoints | `CpuEngine` (`application/cpu.rs`) | `GpuEngine` (`infra/src/gpu/engine.rs`) |
| `Renderer` | resample the sim grid to an output size and run the optics rules | `CpuEngine` via `domain::optics::render` | `GpuEngine` compute pass (`render.wgsl`) |
| `Exporter` | encode an `Image` to bytes | `PngExporter` (`infra/src/export.rs`) | - |

Use cases (`core/src/application/use_cases.rs`) are command-style wrappers over
the ports: `CreateScene`, `UpdateScene`, `ApplyOperation`, `Advance`,
`AdvanceByElapsed`, `ExportFinished`. `Playback<S: Simulator>` (`playback.rs`)
drives a scene through its timeline with play/pause/reset/seek/finish and an
artistic duration mapping, and `Reveal` (`reveal.rs`) builds a standard
mask/wash/drop-in/glaze timeline. Hosts supply elapsed time by calling
`advance_by_elapsed` from their frame clock; the engine never schedules.

## The choreography pass

`application::choreography` (`choreography.rs`) is a timeline-to-timeline
transform that makes a reveal read as drawn. Applied at one choke point,
`authoring::choreograph_scene` (called from `ArtworkCatalogue::by_id_for_with_resolution`
and the legacy `wash`/`crescent_moon`/`glaze_pair` constructors), so the bake,
the native examples and the live path all get the same reveal. Each authored
stroke becomes a narrow, wet tip track walking its path, with a `Water` halo
at the authored radius trailing a few ticks behind for pigment to bloom into.
Invariants: the footprint is unchanged (the halo is the authored stamp),
pigment and water totals are preserved (tip concentration and water scale by
`1/tip_scale`, the authored water is split between tip and halo), and nothing
crosses a `Dry`/`DryAll`/`SetMask`/`ClearMask` boundary. Strokes are sequenced
across a budget of `paint_spread * paint_end`, paced by path length, so the
pen draws one mark after another. `choreograph_scene_with` plus
`by_id_for_unchoreographed` are the seam for tuning.

## Progress curves

`Playback` maps wall-clock progress `p in 0..1` to simulation ticks through
one of three `ProgressCurve`s (`application/playback.rs`):

| Curve | Mapping | Used when |
|---|---|---|
| `FrontLoaded` | `ease(p) = 1 - (1 - p)^2` | the original default: the wash lands fast, then settles |
| `Linear` | `tick = round(p * total_ticks)`, one-for-one | a caller drives `advance_to_progress` with its own easing |
| `Reveal { wall_split, tick_split }` | piecewise linear: `tick_split` of the ticks run inside `wall_split` of the wall clock, the rest over the rest | the default curve for every `Playback` |

`Reveal` separates wall-clock from simulation ticks because the paint phase
needs many ticks inside its ~600 ms while drying needs few over the ~2.4 s
tail; running the whole tail at paint tick rate would blow the frame budget.
`wall_split` defaults to `DEFAULT_PAINT_WALL_FRACTION` (0.2); `tick_split` is
read off the scene by `ProgressCurve::reveal_for` as the tick of the scene's
**last stroke event** — the moment the pen leaves the paper — so the wall-clock
paint phase covers exactly the brushwork and the tail covers the spread, bloom,
settling and drying that follow it. A timeline with no strokes falls back to its
last `Dry`, then to `0.3`, and both splits are clamped to `0.05..0.95` so
neither phase can degenerate to zero length.
On the wasm surface, `setProgressCurve("reveal")` rebuilds the curve for the
loaded scene at the default paint fraction, and `createInstance(..., settleFraction,
paintWallFraction)` overrides the split per instance; the TypeScript `tail`
option is the complement (`1 - tail`) of `paintWallFraction`.

## How the boundary is enforced

- `tests/boundary.rs` in `nocturne-watercolour-core` fails the build if a
  `use wgpu|serde|web_sys|js_sys` appears anywhere in `src/`, if `domain/`
  reaches into `application/`, or if `[dependencies]` in the core manifest is
  non-empty.
- The crate graph is enforced by the dependency declarations themselves:
  infra depends only on core, wasm depends only on core and infra, and the
  TS package depends only on Svelte/Tailwind as peers (no runtime npm deps).

## Explicit mappings

**Domain to scene document.** The domain types are serde-free. `SceneDocumentV1`
(`infra/src/document.rs`) mirrors them field-for-field in plain serde structs
and the `to_document` / `from_document` mappers are explicit, so a schema
change is a deliberate new version rather than a silent field addition.
Operations and masks are externally tagged (`{"brush": {...}}`) because
`serde_json`'s `arbitrary_precision` feature (enabled elsewhere in the
workspace) breaks internally tagged enums on floats. `parse_scene_json` reads
`version` first and rejects anything but `1` with a typed
`UnsupportedVersion` error before the body is parsed. See
[scene-format.md](scene-format.md).

**Scene to GPU state.** Every per-cell field is packed into one `array<f32>`
**state** buffer and one **scratch** buffer (`StateLayout` / `common.wgsl`),
which keeps the port inside WebGPU's default eight storage buffers per stage
and makes a checkpoint a single `copy_buffer_to_buffer`. Each pass reads
`state` and writes its own scratch region, so no pass reads its own output.
The composite mode travels in the state header (`StateLayout::pack` writes
`grid.composite_mode.flag()` into the spare float after `dry_rate`;
`render.wgsl` reads it), so checkpoints and both backends read the mode from
the same state. The engine's `load` builds its grid with
`SimulationGrid::new(..).with_composite_mode(scene.composite_mode())`; until
it does, the GPU renders subtractively and the luminous lockstep test skips.

**Geometry.** The simulation grid is square and is stretched to the scene's
`size_hint`. Grain, pooling octaves, stamps and feathers are measured in an
isotropic metric (`scene::isotropic_scale`: shorter side `0..1`, longer
`0..max(aspect, 1/aspect)`) so they stay round after the stretch. `PaperField::
generate_with_aspect`, `paint::rasterize_path_aspect` / `rasterize_mask_aspect`
and `SimulationGrid::aspect` (packed in the state header) carry the aspect;
the fluid step itself is deliberately square-metric.

**Lucide icons.** A Lucide icon is a 24-grid element list (`[tag, attrs]`
pairs: `path`, `circle`, `rect`, `line`, `ellipse`, `polyline`, `polygon`).
`authoring/svg.rs` flattens each element to subpaths tagged closed/open (the
path grammar `M L H V C S Q T A Z` absolute and relative, cubic/quadratic
subdivision, arcs sampled via the endpoint-to-centre parametrisation), maps
the 24 grid into `0.1..0.9` of the square frame, and paints the hand-authored
pattern: closed subpaths stencilled and filled as `BaseWash` bodies, open ones
laid wet-on-dry as `Shadow` marks at the Lucide stroke radius. An icon with no
closed subpath lays its longest open one as a fat body wash instead. Per-icon
hints (`fill`, `markRadius`, `smallMarks`, `holes`, `bodyRole`, `markRole`)
tune that mapping where the generic one fails - open silhouettes that never
fill, and too many marks at 48 px; `IconHints::default()` is the untuned
behaviour and the TypeScript `ICON_HINTS` table ships the tuned entries. The
JSON crosses the wasm boundary at `iconScene`: the TypeScript side serialises
the element list and the merged hints and passes them to the binding,
`parse_icon_elements` / `parse_icon_hints` parse them, `svg_icon_scene` authors
the scene (id `lucide-<name>-<palette>-<seed>`), and the finished scene JSON
returns for the engine to load.

## Which simulation rules run in shaders

| Shader | Entry points | Rule (CPU reference) | Deviation from CPU reference |
|---|---|---|---|
| `velocity.wgsl` | `velocity` | Curtis UpdateVelocities (`sim::pass_velocity`) | none |
| `pressure.wgsl` | `divergence`, `jacobi_a`, `jacobi_b`, `project` | Curtis RelaxDivergence (`pass_divergence`, `pass_jacobi` x 8 ping-pong, `pass_project`) | none (host copies `q2 -> q` when the iteration count is odd) |
| `flow.wgsl` | `blur_h`, `blur_v`, `advect`, `pool` | Curtis FlowOutward + MovePigment (`pass_blur_h/v`, `pass_advect`), then the free-surface flux (`pass_pool`) | none |
| `transfer.wgsl` | `transfer` | Curtis TransferPigment + evaporation, capillary absorption, drying (`pass_transfer`) | none |
| `capillary.wgsl` | `capillary`, `capillary_wet` | Curtis SimulateCapillaryFlow (`pass_capillary`) | none |
| `apply.wgsl` | `apply_brush`, `apply_water`, `apply_lift`, `dry_all` | `paint::apply_*`, `sim::dry_all` on an uploaded stamp | none |
| `render.wgsl` | `render` | `optics::render`: cubic B-spline reconstruction (16 taps, ~4× the cell reads of bilinear), granulation, mixed KM layer, premultiplied conversion in the mode read from the state header | f32 transcendental precision only |

Stamps and masks are rasterised on the CPU by the shared `domain::paint` code
and uploaded as a coverage field, so both backends see identical geometry. Shared
constants in the shaders (`DRAIN_DEPTH`, `ALPHA_SOFTNESS`, `LUMINOUS_*`, ...)
mirror the `pub const`s in `domain::sim`, `domain::paint` and `domain::optics`;
tunable parameters travel in the `Params` uniform.

### Stamps and masks

`rasterize_mask_aspect` was `O(cells x points)` - distance to every outline
segment for every cell, plus an even-odd cast over every edge. Two culling
passes make it `O(band area + cells)`, both bit-identical: each segment is
walked only over its bounding box expanded by `feather + radius` (a cell
farther than that is `0` for that segment), and the even-odd inside test is
pre-bucketed per row so a cell casts only against the edges its row's y-range
spans; cells outside the polygon's vertex bounding box skip the cast entirely.
The `Path` variant (the one the flattened-SVG feature feeds) runs 20-110x
faster, `Polygon` 1.3-3.5x. Bit-identity is proven by FNV-1a snapshot tests
over 64 synthetic masks (`core/tests/mask_raster.rs`) and over every catalogue
`SetMask` at every detail and both grounds (`infra/tests/mask_hashes.rs`),
captured from the original code and shown to go red under a deliberately wrong
band; degenerate polygons (fewer than three points) fall back to an empty
per-row edge table rather than panicking.

### Known CPU/GPU deviations

- The CPU and GPU are no longer **bit-exact** after the visual-refinement pass:
  the faster, less damped flow makes drying decisions sensitive to
  fused-multiply-add rounding, so a few boundary cells dry one tick apart on
  the two backends. The finished-frame mean absolute difference stays at or
  below 1e-3 (a factor of ten inside the 0.01 tolerance); isolated cells reach
  max differences of 0.07-0.14.
- `render.wgsl` differs from `optics::render` only in f32 transcendental
  precision.

## Checkpoint policy and memory bounds

Checkpoints are taken at tick 0, at every event tick and every `every_ticks`
(32). When the backend's capacity is full the oldest periodic non-event
checkpoint is released, and if none remain no more are taken. Capacity is
`clamp(budget / checkpoint_bytes, 1, 64)`:

- Native budget 256 MB (`nocturne-watercolour-core`): a checkpoint is
  `(10 + 2*pigments) * cells * 4` bytes - 25 MB at 512^2 with 8 pigments
  (10 checkpoints), 4.7 MB at 256^2 with 4 pigments (54).
- Browser budget 48 MB (`nocturne-watercolour-wasm`, a fraction of the native
  budget because several instances share one device and browsers cap GPU
  memory per tab): 10 checkpoints at the catalogue's 256^2 x 4 pigments, about
  44 MB per live instance.

The budget is per instance: `createInstance` takes it as a fifth argument, and
paint drops (`DropSurface`) pass 1 byte unless the showcase scrubber is driving
them, so an unscrubbed drop holds the single tick-0 checkpoint and nothing else.

`Playback` falls back to reload-and-replay from tick 0 when no checkpoint
precedes the seek target. Seeking restores the nearest checkpoint at or before
the target and replays; `seek(t)` + `step` is bit-identical to a straight run
(tested on the CPU engine).

## GPU work bounds

Every buffer, command buffer and dispatch the engine issues is bounded ahead
of the driver (`infra/src/gpu/engine.rs`), so no scene, canvas or replay can
grow one past what the device can carry or run one past the two-second
watchdog Windows resets the display driver at:

- **Allocations are checked against the device limits** before creation
  (`max_buffer_size`, and `max_storage_buffer_binding_size` for storage
  buffers) and refused with an `EngineError`. wgpu reports an oversized buffer
  as an uncaptured error after the fact, which would fault the device for
  every instance sharing it.
- **Ticks are encoded sixteen per command buffer** (`TICKS_PER_SUBMIT`), a few
  tens of milliseconds at the 512^2 maximum on an integrated GPU.
- **The optics pass is dispatched in row bands** of at most 2^20 output pixels
  (`RENDER_PIXELS_PER_DISPATCH`), each its own submission, so a large canvas or
  export raises the number of dispatches rather than the length of one.
- **At most 32 submissions are in flight natively** (`MAX_IN_FLIGHT_SUBMISSIONS`):
  the 33rd blocks on the oldest, so an unattended replay (bake, tests) cannot
  pin unbounded driver memory. The browser paces its own queue.
- **Blocking waits time out** after 30 s (`GPU_WAIT_TIMEOUT`) and return an
  error instead of stalling the process on a wedged driver.
- **Device errors are recorded, not panicked.** The context installs an
  uncaptured-error handler; the first validation/out-of-memory/internal error
  is kept and every later submission returns it (`GpuContext::check`). Natively
  wgpu's default handler would abort the process; in wasm it would leave the
  module unusable. The readback staging buffer is created on first readback,
  so a presenting instance never holds one.

## Future native integration points

`nocturne-watercolour-core` (domain + application) and `-infra` (wgpu + WGSL)
are portable as-is; the wasm crate is ~400 lines of glue. No Kotlin/UniFFI
work exists or is planned in this deliverable - a native host would supply a
backend of the same shape:

- `Simulator`: `load` (allocate state for the sim resolution and palette),
  `apply` (rasterise with `domain::paint`, upload, add), `tick`/`step`,
  `snapshot`/`restore`/`release` with a stated capacity;
- `Renderer`: resample the sim grid to any output size and run the `optics`
  rules (the WGSL in `-infra` is a direct port to copy from, or drive
  `GpuEngine::present` to a `wgpu::SurfaceTarget` via
  `GpuContext::create_window_surface`);
- `Exporter` if the host needs encoded bytes; otherwise consume `Image`
  directly;
- a clock: call `Playback::advance_by_elapsed` from the host's frame callback;
- asset loading and scheduling for the baked/static fallback paths.

The reference list in the infra README gives the same contract from the wgpu
side: allocate the packed state/scratch buffers from `StateLayout`, port the
seven `.wgsl` files (plain WGSL, no extensions), upload stamps from
`domain::paint`, and drive `Playback` from the frame clock.