# @nocturne/watercolour

See [docs/watercolour/](../../../../docs/watercolour/README.md) for the full developer documentation of the watercolour library.

Watercolour artwork for the Nocturne UI. A WebGPU simulation (Rust, compiled to
wasm) paints each artwork on in front of the viewer; where WebGPU is missing,
capped or refused, the same artwork plays from a baked frame strip or shows as a
still PNG. The public API is plain TypeScript; `Artwork.svelte` is a thin wrapper
over it.

The engine itself lives in `crates/nocturne-watercolour-{core,infra,wasm}`. Read
their READMEs for the simulation, the optics and the scene document format.

## Setup and build

```bash
# once per clone, and after any change under crates/nocturne-watercolour-*
pnpm --filter @nocturne/watercolour build:wasm

# regenerate the baked/static fallback assets (needs a GPU; ~15 s)
pnpm --filter @nocturne/watercolour bake

pnpm --filter @nocturne/watercolour check
pnpm --filter @nocturne/watercolour test
```

`build:wasm` runs `cargo build --profile wasm-release --target wasm32-unknown-unknown`
and `wasm-bindgen --target web` into `src/wasm/`, which is gitignored like the
NSwag client: a fresh clone has no bindings until it runs, and `check` fails on
the missing module until then. It prints the module size and runs `wasm-opt -Os`
only if one is on `PATH` (not installed here). Measured: 778 KB, 277 KB gzip.

## Public API

```ts
import { createArtworkPlayer, detectCapabilities, getEngineHost } from '@nocturne/watercolour';

const caps = await detectCapabilities(); // { webgpu, adapter, reducedMotion, offscreenCanvas, reason? }

const player = createArtworkPlayer(
  canvas,
  { id: 'crescent-moon', palette: 'moonlight', seed: 42, intensity: 0.7, surface: 'dark' },
  { durationMs: 600, mode: 'auto', motion: 'auto', autoplay: 'once', width: 256, height: 256, dpr: window.devicePixelRatio },
);
player.on('ready', () => console.log(player.state.mode)); // 'live' | 'baked' | 'static' | 'none'
player.on('finished', () => {});
player.on('fallback', ({ from, error }) => {}); // a backend failed; a lower one took over
player.on('error', (error) => {}); // nothing could draw; error.code is typed

player.play(); player.pause(); player.reset(); player.seek(0.5); player.finishImmediately();
player.resize(320, 320, window.devicePixelRatio);
const png = await player.exportPng(512, 512); // live only
const { strip, manifest } = await player.exportStrip(12, 256); // live only
player.dispose();
```

The second argument is a `SceneSource`: an `ArtworkRef` as above, or
`{ sceneJson }` / a JSON string holding a versioned scene document (only the
`version` field is checked here; the engine validates the rest).

`surface: 'light' | 'dark'` is the page background the artwork sits on. `dark`
keeps the chosen palette and switches the scene to luminous compositing
(`Background::TransparentOnDark`), so pigment reads as light on a dark ground;
it does not select the `<palette>_dark` palettes, which are subtractive variants
for hosts that composite over a dark ground themselves. The Svelte component
defaults it from a `.dark` class on `<html>`, else `prefers-color-scheme`.
`detail: 'small' | 'medium' | 'large' | 'extraLarge'` simplifies the artwork for
its rendered size; `detailForEdge(px)` picks it from the canvas's BACKING long
edge (CSS size times device pixel ratio, capped at 2): below 64 px small, below
192 px medium, below 320 px large, 320 px and above extraLarge. The simulation
grid is the tier's own (96/160/256/384) whatever the canvas size: the fluid
moves in cells, so a different grid would paint a different picture, and a tier
paints the same one at every size. An explicit `detail` or `simResolution`
(live only) option overrides the derived values.

`easing: (t: number) => number` maps wall-clock progress to the progress shown,
e.g. `import { cubicOut } from 'svelte/easing'`. When given, the live backend
drives the engine with `advanceToProgress(easing(t))` on a linear curve and the
baked backend eases its frame index the same way; the `progress` reported stays
wall-clock. Absent, the engine's built-in front-loaded curve
(`ease(p) = 1 - (1 - p)^2`, wash lands fast, then settles) runs the reveal.

`tail: number` (0..1, default 0.3) lengthens the reveal's ending: live scenes
spend the final `tail` fraction of their ticks settling and drying (the settle
phase starts at `1 - tail`), with the evaporation rate scaled inversely with the
tail so the drying keeps going to the end — water leaving, the rim darkening,
the last soft edges firming up — instead of the scene reaching full dryness
early and holding a static frame; baked reveals hold the finished frame for the
last `tail` of the duration.

`state` is a snapshot (`mode`, `motion`, `playing`, `finished`, `progress`,
`error`, `fallbackReason`); it is never pushed per frame. Listen to
`statechange`, `finished` and `fallback`, or poll on your own timer.

Other exports: `EngineHost` (`getEngineHost()`, `configureEngineHost({ maxLiveInstances })`,
`stats()`), `Scheduler` (`getScheduler().stats()` for the frame-time histogram),
`resolveMode` / `fallbackOrder`, the baked helpers (`parseBakedManifest`,
`stripFramePosition`, `loadStrip`, `drawStripFrame`), `assetUrl`, and the types
from the component contract (`ArtworkId`, `PaletteId`, `ArtworkOptions`,
`ARTWORK_IDS`, `PALETTE_IDS`, `seedFromName`).

### Svelte

```svelte
<script lang="ts">
  import { Artwork } from '@nocturne/watercolour';
</script>

<Artwork artwork="crescent-moon" palette="moonlight" seed={42} surface="dark" class="size-32" />
```

The component sizes its canvas to the container (`ResizeObserver`, DPR capped at
2). `fit="contain"` (default) keeps the artwork's natural aspect by sizing the
canvas to the largest box of that aspect that fits and centring it, leaving the
surrounding area transparent; `fit="fill"` stretches to the container. The
accent components (`PaintedUnderline`, `SelectionEdge`, `AvatarWash`,
`ConfirmationBackground`, `HeaderMotif`) take the same `fit` prop; the artwork
is `aria-hidden` with `role="presentation"`. The player is created in an effect
and disposed on destroy or when any prop changes. It maps props to
`createArtworkPlayer` options and nothing else.

## Paint drops

`DropSurface` wraps arbitrary content and paints abstract marks in the space
**around** it on hover, selection or focus; `DropGroup` stops a run of them
repeating itself. The marks are placed by ink rather than by element box, the
copy is measured off the DOM with `@chenglou/pretext`, and a baked mark arrives
through a radial mask spreading from where the brush touched down.

```svelte
<DropGroup name="feature cards">
  <DropSurface
    name="Your server, your data"
    fonts={{ title: { font: '600 14px "Cabin", sans-serif', lineHeight: 20 } }}
    class="rounded-xl border bg-card"
    contentClass="flex items-start gap-3 p-4"
  >
    <div data-drop-obstacle><Icon /></div>
    <h3 data-drop-text="title" class="text-sm font-semibold">Your server, your data</h3>
  </DropSurface>
</DropGroup>
```

The full contract - the three-layer stack, the placement rules, the reveal
curves and both colour directions - is in
[`docs/watercolour/public-api.md`](../../../docs/watercolour/public-api.md#paint-drops).
`/drops` in the showcase is the working reference.

## Modes and fallbacks

| Mode | Draws with | Needs | When chosen (`mode: 'auto'`) |
|---|---|---|---|
| `live` | WebGPU via the wasm engine, presented straight to the canvas | `navigator.gpu`, an adapter, a free slot under `maxLiveInstances` (default 4) | Preferred when available and motion is not reduced |
| `baked` | 2D canvas, cross-fading two frames of a PNG strip | the strip + manifest asset (bundled or `assetBaseUrl`) | No usable GPU or the instance cap is reached |
| `static` | 2D canvas, the finished PNG drawn once | the final asset | Reduced motion (first choice), or nothing else works |
| `none` | nothing | - | No GPU and no asset; `error` fires with a typed code |

Explicit modes fall down the same chain when unavailable (`live` -> `baked` ->
`static` -> `none`; `static` with no still uses the strip's last frame). A backend
that dies at runtime (device lost, render error) emits `fallback` and the player
moves down the chain without retrying live. Reduced motion (`motion: 'reduced'`,
or `'auto'` with `prefers-reduced-motion`) shows the finished frame at once: static
if an asset exists, else the live engine finished immediately, else the strip's
last frame. `releaseAfterFinish` makes live finish on first appearance, present
one frame and then dispose the engine instance (the canvas keeps the pixels), so
the artwork holds no live slot or checkpoints; under reduced motion it also lets
`auto` pick live over the identical baked still, which is what gives `AvatarWash`
its per-name wash. `autoplay: 'once'` starts on first appearance (the shared
`IntersectionObserver`) and never loops; `'never'` waits for `play()`.

A canvas that has produced a WebGPU context cannot produce a 2D one, so a fallback
after live ran replaces the element in place (`player.canvas` is the current one).

## Baked format

`strip.png` holds `frames` equal frames stacked vertically, evenly spaced in
artistic progress with the last frame finished and dry; `strip.json` describes it:

```json
{ "version": 1, "frames": 12, "width": 256, "height": 256, "durationMs": 600, "layout": "vertical" }
```

Hard caps: 16 frames, 256 px per frame edge. The strip decodes once with
`createImageBitmap` (one decode, one upload); each frame is two `drawImage`
calls with `globalAlpha` cross-fading the neighbours, so memory is the strip
bitmap only (at the caps 16 x 256 x 256 x 4 = 4 MB, the shipped 12 x 256 strips
are 3 MB decoded). The cross-fade through transparent pixels is not a true
linear blend; at 12 frames the difference is not visible. `durationMs` in the
manifest is the artwork's suggested reveal length; the player's option wins.

The shipped assets live under `assets/<artwork>/<palette>[_dark]/` as
`final-512.png`, `final-128.png`, `strip.png`, `strip.json`, one set per
catalogue artwork. The full catalogue is too large to check in (17 artworks x
6 palettes x 2 surfaces is ~100 MB), so the bundle ships a **curated subset**:
each artwork in one default palette x both surfaces, baked at seed 1610,
intensity 0.7 and `large` detail by
`crates/nocturne-watercolour-wasm/examples/bake_catalogue.rs` driven by
`scripts/bake-manifest.json`:

| Artwork | Palette |
|---|---|
| `crescent-moon`, `moonlit-shoreline`, `header-motif`, `alarm-bell` | `moonlight` |
| `magnifying-glass`, `connected-shores`, `avatar-wash`, `selection-edge` | `water` |
| `overlapping-shapes`, `linked-rings` | `dusk` |
| `confirmation-mark`, `confirmation-background` | `moss` |
| `report-pages`, `distant-mountains` | `slate` |
| `tab-underline` | `ember` |

Strips are 10 frames at the artwork's natural aspect (icons and accents up to
192 px, the four scenes up to 256 px, within the 16-frame / 256-px caps);
finals are the long edge 512 / 128 px at the same aspect. The set is ~6 MB
total and is tracked in git so the baked fallback works on a fresh clone with
no GPU. To regenerate or change it, edit `scripts/bake-manifest.json` and run
`pnpm --filter @nocturne/watercolour bake`; to bake the whole catalogue or one
artwork instead, run the example directly with `bake_catalogue <out_dir>` or
`bake_catalogue <out_dir> <id>`. Serve a different bake via `assetBaseUrl` or
explicit URLs through `assets`.

**Palette fallback.** Only the default palette per artwork is baked, so a
request for a palette that is not bundled falls back to the default palette's
assets for that artwork (same surface): `assets.ts` resolves a missing
`<palette>[_dark]` directory to `DEFAULT_PALETTE[artwork][_dark]`. The table
above is the source of truth; `defaultPaletteFor(id)` exposes it, and a test
asserts it stays in sync with `scripts/bake-manifest.json`. The fallback applies
to the bundled set only - an `assetBaseUrl` is served exactly as requested.

## Performance

Measured on Chrome 153, Windows 11, RTX 5060 Laptop GPU, at a 512 x 512 canvas
with a 256 x 256 simulation grid (the showcase defaults), unless noted.

- **wasm module**: 778 KB, 277 KB gzip (`build:wasm` reports the live sizes).
- **GPU init** (adapter + device + pipeline compile, one per page): 59-90 ms
  warm; about 2.1 s cold. Reported as `engine.stats().initMs`.
- **First painted frame**: 1.1-1.6 s warm after a cold `createArtworkPlayer`,
  from `performance.mark` around module load, engine init and the first
  presented frame (the showcase exposes it as `watercolour:first-paint`).
- **Steady-state scheduler cost**: about 0.2 ms per frame (CPU step + render
  submission, measured with `performance.now`); the scheduler keeps a frame-time
  histogram (`getScheduler().stats()`).
- **Checkpoints**: about 44 MB per live instance in the browser (10 checkpoints
  at the catalogue's 256^2 x 4 pigments); `engine.stats().checkpointBytes`
  reports the live total.
- **Baked assets**: the curated set is 5.98 MB on disk (30 sets: 15 artworks x
  2 surfaces; strips average ~120 KB, 512 px finals ~78 KB, 128 px finals ~7 KB).

Methodology: `performance.mark`/`performance.now` timestamps around module load,
`WatercolourEngine.create`, first paint and each scheduler tick; the scheduler
records a histogram of frame times rather than a per-frame push. Numbers are
single-run spot checks on the machine above, not a benchmark harness.

## Resource limits

- One WebGPU device per page (`EngineHost`), compiled pipelines shared by every instance (`GpuEngine::fork`).
- `maxLiveInstances` (default 4): the fifth `auto` artwork resolves to baked.
- Per-instance checkpoint budget in the browser: 48 MB (10 checkpoints at the catalogue's 256^2 x 4 pigments), against 256 MB natively. `engine.stats().checkpointBytes` reports the live total.
- One rAF loop for the page; hidden documents stop it, off-screen artworks are neither stepped nor rendered, a stalled frame is clamped to 250 ms.
- Component DPR is capped at 2.
- Exports are async (`map_async` cannot block in a browser) and `exportStrip` leaves the playback finished.
- Timings in `stats()` are CPU submission times from `performance.now()`, not GPU completion.

## Native integration points

`nocturne-watercolour-core` (domain + application) and `-infra` (wgpu + WGSL) are
portable; the wasm crate is ~400 lines of glue. A native host implements the same
three things this package does around them: create a `GpuContext`, attach a
`PresentSurface` (`GpuContext::create_window_surface` takes any
`wgpu::SurfaceTarget`; `GpuEngine::present` blits the frame with `present.wgsl`),
and resolve assets for the baked/static paths. `PngExporter`, `FrameSequence` and
the `scene_tools` module (catalogue lookup, intensity, strip stitching, manifest)
run unchanged on the host.

## Dependencies and licences

Rust (see `crates/Cargo.lock` for exact versions): wgpu 30 (MIT OR Apache-2.0),
wasm-bindgen / js-sys / web-sys / wasm-bindgen-futures (MIT OR Apache-2.0), png
0.18 (MIT OR Apache-2.0), serde / serde_json (MIT OR Apache-2.0), bytemuck (Zlib OR
Apache-2.0 OR MIT), console_error_panic_hook (Apache-2.0 / MIT). No runtime npm
dependencies; Svelte >= 5 and Tailwind >= 4 are peers.
