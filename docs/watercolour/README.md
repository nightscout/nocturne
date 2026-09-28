# Watercolour graphics library

A watercolour graphics engine for the Nocturne UI. A WebGPU simulation (Rust,
compiled to wasm) paints each artwork on in front of the viewer; where WebGPU
is missing, capped or refused, the same artwork plays from a baked frame strip
or shows as a still PNG. The public surface is plain TypeScript plus thin
Svelte components.

The engine is an **independent implementation** of the watercolour simulation
described in Curtis, Banks and Beier 1997, *Computer-Generated Watercolor*
(see [provenance-and-licences.md](provenance-and-licences.md)).

## Repository layout

| Path | Contents |
|---|---|
| `crates/nocturne-watercolour-core/` | Domain model, CPU reference simulation, Kubelka-Munk optics, application ports and use cases. `std`-only. |
| `crates/nocturne-watercolour-infra/` | wgpu/WGSL simulation and rendering, versioned serde scene documents, PNG export, the artwork catalogue, Lucide icon authoring (`authoring/svg.rs`). |
| `crates/nocturne-watercolour-wasm/` | wasm-bindgen web adapter (thin). |
| `src/Web/packages/watercolour/` | `@nocturne/watercolour` - the TypeScript API, Svelte components, baked/static assets, wasm output, per-icon hints (`src/api/icon-hints.ts`). `lucide` is a peer dependency: hosts supply icon element lists. |
| `src/Web/packages/watercolour-showcase/` | `@nocturne/watercolour-showcase` - SvelteKit showcase app on port 5181. |

## What is where

- **Architecture** - the Clean Architecture layering, the ports and use cases,
  the domain/scene-document/GPU-state mappings, which simulation rules run in
  shaders, and the native integration points: [architecture.md](architecture.md)
- **Scene format** - `SceneDocumentV1` field by field, the baked strip format,
  the static PNG export, the curated asset set: [scene-format.md](scene-format.md)
- **Pigment and compositing** - the Kubelka-Munk model, the subtractive and
  luminous output modes, the paper model, numerical limits:
  [pigment-and-compositing.md](pigment-and-compositing.md)
- **Public API** - the TypeScript API, the Svelte components, mode/motion
  resolution, resource limits, and a host-app example:
  [public-api.md](public-api.md)
- **Verification** - what is tested where, measured performance, known
  limitations: [verification.md](verification.md)
- **Provenance and licences** - the independent-implementation statement and
  every dependency's licence: [provenance-and-licences.md](provenance-and-licences.md)
- **Authoring portability** - Lucide icons as watercolour scenes (the
  closed-subpath-body / open-subpath-mark mapping, per-icon hints), the
  JSON-vs-Rust authoring decision, and the mask rasteriser acceleration:
  [authoring-portability.md](authoring-portability.md)

## Setup and build

Rust (edition 2024, workspace at `crates/`) and Node/pnpm for the web
packages. The wasm target and the bindings CLI are needed once:

```bash
rustup target add wasm32-unknown-unknown
cargo install wasm-bindgen-cli --version 0.2.128
```

Build the wasm bindings into `src/Web/packages/watercolour/src/wasm/` (needed
after any change under `crates/nocturne-watercolour-*`, and on a fresh clone,
which has no bindings):

```bash
cd src/Web
pnpm --filter @nocturne/watercolour build:wasm
```

Regenerate the baked/static fallback assets (needs a GPU; ~15 s for the
curated set):

```bash
pnpm --filter @nocturne/watercolour bake
```

Run the showcase app (serves on port 5181):

```bash
cd src/Web
pnpm --filter @nocturne/watercolour-showcase dev
```

### Tests

```bash
# Rust, from crates/
cargo test -p nocturne-watercolour-core
cargo test -p nocturne-watercolour-infra
cargo test -p nocturne-watercolour-wasm
# The full reveal sweep, ignored by default (about an hour)
cargo test -p nocturne-watercolour-infra --release --test reveal_preserves_the_artwork -- --ignored

# Web, from src/Web/
pnpm --filter @nocturne/watercolour check
pnpm --filter @nocturne/watercolour test
pnpm --filter @nocturne/watercolour-showcase check
pnpm --filter @nocturne/watercolour-showcase test
pnpm --filter @nocturne/watercolour-showcase build
```

The engine-level tests in `nocturne-watercolour-infra` need a GPU; without an
adapter they print and return early.

## Delivery status

Tracked in `docs/plans/2026-09-17-watercolour-plan.md` (progress log with
decisions and measured numbers):

| Stage | State |
|---|---|
| 1. Engine (core + infra) | **Implemented + tested**: cubic B-spline reconstruction, four detail tiers (96/160/256/384 + live override to 512), Luminous compositing, aspect-aware geometry. `cargo test`/`clippy`/`fmt` green, CPU-GPU lockstep. |
| 2. wasm adapter + TS core API | **Implemented + tested**: wasm bindings built, TS player/capabilities/host/scheduler/mode resolution tested; verified live in Chrome 153 (wasm 778 KB / 277 KB gzip, warm init 59-90 ms). |
| 3. Components + artwork catalogue + export/baked pipeline | **Implemented + tested**: 15 catalogue ids, curated baked assets (5.98 MB, tracked), palette fallback, accent components (fit prop, avatar release, dark opacity). Host-app reports-page integration added. |
| 4. Showcase pages | **Implemented + tested**: twelve routes wired to the real package, `check`/`test`/`build` clean. Browser pass done (see verification). |
| 5. Visual verification, performance measurement, docs | **Browser-verified** (Chrome 153, all routes; screenshots in `.playwright-mcp/stage3/`); visual review done (light surfaces strong, dark surfaces murky - see limitations); performance measured (native + browser). Multi-instance measurement and the live reveal at the biggest grids are the unverified remainder. |
| 6. Lucide icons | **Implemented + tested**: `svg_icon_scene`/`iconScene` map a Lucide element list onto the stencil-and-mark pattern with per-icon hints, the showcase Playground plays them, and twelve Lucide icons (eleven wishlist proposals plus `cpu`) are baked into the curated asset set (96 files / 4.6 MB). |

## Limitations

In plain language, what this library does not yet do well:

- **Dark-surface artwork reads flat or murky.** The Luminous tuning that shipped
  keeps bodies continuous but flat; the translucent alternatives that add depth
  also bring back grey dirt. The fix belongs in the alpha and grain curves.
- **The biggest live heroes can drop below 60 fps.** The 600 ms reveal at a 512
  simulation grid can cost more than a 16 ms frame on large canvases. The
  scheduler stays correct, it just cannot keep a smooth 60 fps there.
- **Baked assets stay at their baked detail.** The resolution override is live
  mode only.
- **The host app has 46 pre-existing `svelte-check` errors** unrelated to this
  library (stale generated client); the watercolour integration adds none.
- **Build agents could not view images**, so visual verdicts came from the
  coordinator and a dedicated vision review.
- **The wasm module needs `wasm-opt` on the build machine** (`npm install -g
  binaryen`): with it the module is 652,953 B / 274,570 B gzip; without it the
  build script skips the pass and ships 966,642 B / 339,648 B gzip.
- **The catalogue-wide reveal sweeps are `#[ignore]`d**: the full
  `reveal_preserves_the_artwork` run takes about an hour in `--release` and is
  not feasible unoptimised, so the default run checks one artwork and the sweep
  runs with `--release -- --ignored` (see `verification.md`).

## The idea in one paragraph

Every artwork is a **scene**: a paper sheet, a palette of pigments with
Kubelka-Munk absorption/scattering coefficients, and a timeline of brush,
water, lift and dry operations on a fixed-timestep simulation grid. A
checkpointed playback steps the grid, and a renderer turns the finished cells
into a premultiplied-linear-RGBA image via a per-pixel mixed pigment layer. The
CPU reference (`nocturne-watercolour-core`) and the GPU (`nocturne-watercolour-infra`)
are two implementations of the same ports with the same rules; the wasm crate
adapts the GPU engine for the browser, and the TypeScript package decides at
runtime whether to draw live, from a baked strip, or from a still.