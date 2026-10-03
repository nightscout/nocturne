# Scene document format

A scene is a complete, self-contained description of one artwork: a paper
sheet, a palette of pigments, and a timeline of operations on a fixed-timestep
simulation grid. Everything an artwork looks like is derivable from a scene and
its seed.

## `SceneDocumentV1`

Defined in `crates/nocturne-watercolour-infra/src/document.rs`. The domain
types are serde-free; `to_document` / `from_document` are explicit mappers and
the enums are externally tagged. `parse_scene_json` reads the `version` field
first and rejects a newer or missing version with a typed error before the
body is parsed.

```json
{
  "version": 1,
  "id": "crescent-moon-moonlight-1610",
  "size_hint": [512, 512],
  "seed": 1610,
  "sim_resolution": 256,
  "background": "transparent",
  "paper": {
    "seed": 1610,
    "grain_scale": 140.0,
    "height_amplitude": 0.08,
    "absorbency": [0.4, 0.9],
    "fibre_anisotropy": 0.35
  },
  "palette": {
    "name": "moonlight",
    "entries": [
      { "role": "base_wash", "pigment": { "name": "...", "k": [...], "s": [...], "density": ..., "staining_power": ..., "granulation": ... } }
    ]
  },
  "timeline": {
    "total_ticks": 320,
    "events": [ { "at_tick": 0, "op": { "brush": { "path": [[x, y], ...], "radius": [r0, r1], "pigment": 0, "concentration": ..., "water": ..., "softness": ..., "span": [0.0, 1.0] } } } ]
  }
}
```

(The `paper` numbers above are illustrative; the mappers pass values through
unchanged.)

### Fields

| Field | Meaning |
|---|---|
| `version` | `1`. Required. `from_document` rejects any other value with `UnsupportedVersion { found, supported: 1 }`. |
| `id` | Free-form string; conventionally `<artwork>-<palette>-<seed>`. Not validated. |
| `size_hint` | `[width, height]` in output pixels. The square sim grid is stretched to this on render. |
| `seed` | `u64`. Every artwork derives all randomness from it (see Determinism below). |
| `sim_resolution` | Edge length of the square simulation grid (e.g. 96/160/256/384 by detail level). Validated. |
| `background` | `"transparent"` (subtractive compositing, light hosts) or `"transparent_on_dark"` (luminous compositing, dark hosts). **Defaults to `"transparent"`** when absent, so documents written before the field existed still parse. Unknown values are `UnknownBackground`. |
| `paper` | `seed`, `grain_scale`, `height_amplitude`, `absorbency` (`[min, max]`), `fibre_anisotropy`. |
| `palette` | `name` plus `entries`, each a `role` (`base_wash`/`shadow`/`accent`/`glow`) and a `pigment` (name, Kubelka-Munk `k`/`s` per RGB channel, `density`, `staining_power`, `granulation`). Unknown roles are `UnknownRole`. Up to `MAX_PIGMENTS = 8`. |
| `timeline` | `total_ticks` and `events`, each `{ at_tick, op }`. |

### Operations (`OperationDoc`)

All externally tagged with `snake_case` variants. Geometry is in `0..1` scene
coordinates (measured in the isotropic metric - see Architecture).

| Variant | Fields |
|---|---|
| `brush` | `path: [[x, y]]`, `radius: [start, end]`, `span: [start, end]`, `pigment: usize` (index into palette), `concentration`, `water`, `softness` |
| `dab` | `center: [x, y]`, `radius`, `pigment`, `concentration`, `water`, `softness` |
| `water` | `path`, `radius`, `span`, `water`, `softness` |
| `lift` | `path`, `radius`, `span`, `strength`, `softness` |
| `dry` | `rate` |
| `dry_all` | - |
| `set_mask` | `mask`: `{ polygon: { points, feather } }` or `{ path: { points, radius, feather } }` |
| `clear_mask` | - |

`dab` deposits one point brush with a uniform radius. It retains the brush's
seeded radius jitter, paper-grain edge and outward flow. The CPU reference
rasterises that brush; the GPU evaluates its coverage without a stamp upload.

### `span` on brush, water and lift strokes

`span` is an arc-length window `[start, end]` (both `0..1`) over the stroke's
**whole** path: the operation lays down only the part of the path the span
covers, as `max(0, coverage_upto(end) - coverage_upto(start))`. Consecutive
spans of one stroke tile `0..1`, so the union of a stroke's spans deposits
exactly what laying the whole path at once would; radius, jitter and paper
shift are evaluated against the full path in both terms so adjacent spans join
without a seam. The field is optional and defaults to `[0, 1]` — the whole
path — so pre-span documents still load unchanged. The choreography pass (see
Architecture) splits each authored stroke into a narrow, walking span to make
the reveal draw the brush along the path instead of stamping the stroke whole.

## Timeline semantics

Tick `t` means `t` steps have run. Events at the current tick apply **before**
the step; events at `total_ticks` apply at the end, followed by an implicit
`DryAll`. `duration_ms` maps progress `p` to ticks through
`ease(p) = 1 - (1 - p)^2`, so half the duration runs three quarters of the
ticks (the wash lands fast, then settles). `Reveal` builds the standard
shape: mask, wash at tick 0, wet-into-wet drop-ins, a wet-on-dry glaze phase
(which dries everything first), settle, and the implicit `DryAll` at the end.
The catalogue scenes stencil several shapes in sequence, each dried before the
next, so they schedule events directly.

## Seeds and determinism

- All randomness derives from the scene's `seed` through `Seed(u64)` and
  `SubSeed` purposes; `seedFromName` on the web gives a stable FNV-1a hash per
  name so the same person always receives the same avatar wash.
- **Bit-identical**: the same seed, scene and tick count on the same backend
  produces identical grids (tested). Seeking to a checkpoint and replaying is
  bit-identical to a straight run (tested on the CPU engine).
- **Not bit-identical across devices/backends**: the CPU reference and the GPU
  can dry a few boundary cells one tick apart under fused-multiply-add
  rounding. The finished-frame mean absolute difference stays at or below
  1e-3 (see verification). Determinism guarantees are per-backend, not
  cross-backend.
- The fixed timestep (`DT = 1` per tick, no variable stepping) is what makes
  any determinism statement possible at all.

## The icon request (`iconScene`)

`iconScene` builds a `lucide-<name>-<palette>-<seed>` scene from a Lucide icon
instead of a catalogue id. Its arguments are the element list JSON, the icon
`name`, then the same `seed`/`palette`/`intensity`/`detail`/`surface`/
`simResolution` as `catalogueScene`, and the hints JSON (`""` keeps the
defaults).

A live player does not take either document: `createCatalogueInstance` and
`createIconInstance` on the engine take the same arguments followed by
`createInstance`'s, and author the scene straight into the playback, so it never
crosses to JavaScript as JSON. `createInstance` with the document plays the same
scene to the bit (tested); a build without the direct constructors is driven
through the document.

**The element list** is the vanilla `lucide` package's `IconNode` shape: a
`[tag, attrs]` pair per element in a 24-grid stroke-drawn icon. The authoring
layer parses `path`, `circle`, `rect`, `line`, `ellipse`, `polyline` and
`polygon` (the path grammar `M L H V C S Q T A Z` absolute and relative),
flattens them to subpaths tagged closed or open, and paints closed subpaths as
stencil-and-fill bodies, open ones as wet-on-dry marks. The 24 grid maps to
`0.1..0.9` of the square frame.

**The hints JSON** tunes that generic mapping per icon. Every field is optional
and an absent one keeps the default:

| Field | Default | Meaning |
|---|---|---|
| `fill` | `[]` | Indices of open subpaths (flattened order) to close and paint as bodies |
| `markRadius` | `1.0` | Multiplier on the Lucide stroke half-width for marks |
| `smallMarks` | `4` | Marks kept at `small` detail (longest first) |
| `holes` | `[]` | Circles lifted out of the wet body before it glazes, in 24-grid units: `[cx, cy, r]` |
| `bodyRole` | `"base_wash"` | Pigment role for bodies |
| `markRole` | `"shadow"` | Pigment role for marks |

The library ships a built-in table for the icons that need it (`ICON_HINTS` in
`src/api/icon-hints.ts`, mirrored by `scripts/icon-hints.json`); the caller's
hints merge over it per field. The scene a request produces is a normal
`SceneDocumentV1` and flows through the same pipeline as a catalogue scene.

## The baked strip format

`strip.png` holds `frames` equal frames stacked vertically, evenly spaced in
artistic progress with the last frame finished and dry; `strip.json` describes
it:

```json
{ "version": 1, "frames": 10, "width": 192, "height": 192, "durationMs": 600, "layout": "vertical" }
```

- **Caps**: 16 frames, 256 px per frame edge (enforced in both
  `scene_tools::stitch_vertical` and the TypeScript `parseBakedManifest`). At
  the caps the decoded bitmap is 16 x 256 x 256 x 4 = 4 MB; the shipped strips
  (10 frames, artwork-natural aspect) decode to under ~1.5 MB each.
- **Decoding and memory tradeoff**: the strip decodes once with
  `createImageBitmap` (one decode, one GPU upload); each frame is two
  `drawImage` calls with `globalAlpha` cross-fading the neighbours, so memory
  is the strip bitmap only. The cross-fade through transparent pixels is not a
  true linear blend; at 10-12 frames the difference is not visible.
- `durationMs` in the manifest is the artwork's suggested reveal length; the
  player's `durationMs` option wins.

## Staged artworks

`hub-dawn-ridges` (the setup hub's header, 3:1) is painted in six equal
stages, one per hub item. Each stage is a whole layer of the picture and is
choreographed and settled inside its own window, with a `DryAll` on the tick
before the next begins, so tick `k/6` of the timeline is always the painting
after exactly `k` stages, dry (`authoring::Stages`, `choreograph_stages`; the
`hub` tests pin it).

That holds in **tick** space. The player's default curve spends the first
fifth of the wall clock on all the painting, so a host that steps through the
stops must pass `easing: (t) => t`, which switches the engine to
`ProgressCurve::Linear`; `seek(k / 6)` then lands on stop `k`. The baked strip
matches: the bake reads the stage count from `ArtworkCatalogue::stages`, gives
the strip one frame per stop (seven) and samples it linearly, so strip frame
`k` is stop `k` and a linear seek on the baked fallback shows the same
painting. A contact sheet of every stop, light
and dark, is in `docs/watercolour/hub-scenes/` (one row per stop, 0 at the
top; light ground on the left, dark on the right), regenerated with the
`render_stops` infra example.

Every layer of a hub scene is laid in rows that follow its band (sky top,
ridge line, water floor) inside a stencil left deliberately loose, so the wash
ends on the brush's own soft, staggered edge rather than piling pigment
against a stencil boundary; the stencil is kept tight only where an edge
should be crisp, such as a ridge crest or the horizon.

## Static PNG export

`PngExporter` encodes a premultiplied-linear-RGBA `Image` to **straight-alpha
sRGB 8-bit** PNG. Colour is divided back out of alpha before encoding; pixels
with `alpha < 1/1024` are written as transparent black (dividing those would
only amplify quantisation noise into colour fringes). The `final-512` /
`final-128` assets are the finished, fully dry artwork at the artwork's
natural aspect (long edge 512 / 128 px).

Those PNGs are intermediates. `scripts/to-webp.mjs` re-encodes every one as
WebP at quality 82 and deletes it, which is what `pnpm bake` runs and what the
loader asks for. These washes are mostly soft alpha, which PNG stores badly.
`alphaQuality` is 100, so libwebp keeps the alpha plane lossless - alpha is
what carries the shape here, and only the pigment colour is approximated.

## The curated asset set

The full catalogue (every artwork x 6 palettes x 2 surfaces) is too large to
check in, so the bundle ships a **curated subset**: each of the 50 artworks and
icons in the manifest in one default palette x both surfaces, baked at seed
1610, intensity 0.7 and `large` detail. The set is **5.4 MB on disk** (400
files: 100 sets x 4 files) and is tracked in git so the baked fallback works on
a fresh clone with no GPU. It was 20.3 MB as PNG.

Assets live under `assets/<artwork>/<palette>[_dark]/` as `final-512.webp`,
`final-128.webp`, `strip.webp`, `strip.json`. `<palette>` is the built-in name
for light surfaces and `<palette>_dark` is the same palette composited in
luminous mode for dark ones. Baked icons live under `assets/lucide-<name>/...`
(the twelve baked sets are 96 files of the total).

**Palette fallback rule.** Only the default palette per artwork is baked. A
request for a palette that is not bundled falls back to the default palette's
assets for that artwork (same surface): `assets.ts` resolves a missing
`<palette>[_dark]` directory to `DEFAULT_PALETTE[artwork][_dark]`. The table in
`scripts/bake-manifest.json` (mirrored by `DEFAULT_PALETTE` in `assets.ts`, and
a test asserts they stay in sync) is the source of truth. Icons resolve through
the same rule: `iconAssetKey(name)` produces the `lucide-<name>` key and a
missing palette falls back to the icon's default. The fallback applies to the
bundled set only - an `assetBaseUrl` is served exactly as requested.

| Artwork | Default palette |
|---|---|
| `crescent-moon`, `moonlit-shoreline`, `header-motif`, `alarm-bell` | `moonlight` |
| `magnifying-glass`, `connected-shores`, `avatar-wash`, `selection-edge` | `water` |
| `overlapping-shapes`, `linked-rings`, `hub-dawn-ridges` | `dusk` |
| `confirmation-mark`, `confirmation-background` | `moss` |
| `report-pages`, `distant-mountains` | `slate` |
| `tab-underline` | `ember` |

The baked icons (the twelve wishlist subjects, from the same manifest):

| Icon | Default palette |
|---|---|
| `lucide-database`, `lucide-server`, `lucide-cpu` | `slate` |
| `lucide-fingerprint`, `lucide-battery` | `water` |
| `lucide-sprout`, `lucide-scale` | `moss` |
| `lucide-syringe`, `lucide-flag`, `lucide-megaphone`, `lucide-rocket` | `ember` |
| `lucide-book-open` | `moonlight` |

## Regenerating the assets

The curated set is driven by `scripts/bake-manifest.json` (artworks, default
palettes, both surfaces, per-artwork strip frame size). To regenerate or change
it, edit the manifest and run:

```bash
cd src/Web
pnpm --filter @nocturne/watercolour bake   # curated set (needs a GPU, ~15 s)
```

The same example can bake the whole catalogue (`bake_catalogue <out_dir>`) or
one artwork (`bake_catalogue <out_dir> <id>`). Serve a different bake via
`assetBaseUrl` or explicit URLs through `assets`.
