# Authoring portability: Rust, JSON, SVG and Lucide

Spike, 2026-09-19. Question: is there a better way to define artworks than Rust
functions, could they be JSON or ride an existing standard, and can the app's
existing Lucide icon set feed the engine so icons stop being hand-authored?

## Verdict

- **Lucide icons work.** A Lucide icon's SVG element list maps mechanically onto
  the hand-authored pattern (closed subpath = stencilled body, open subpath =
  wet-on-dry mark). Of eighteen icons tried, ten read correctly at 48 px as
  generated, seven need mark-thickness or fill tuning, none is structurally
  impossible. Two Lucide-derived icons (`calendar`, `bell`) read better than
  their hand-authored counterparts. This is now in the library (`svg_icon_scene`,
  wasm `iconScene`, TS `{ icon, name }` source, `Artwork icon=` prop) and in the
  showcase Playground.
- **JSON is the wrong target for the procedural scenes.** The nine wide scenes and
  accents are seeded, procedural geometry (crescent, hills, water body, wobble);
  expressing them as a document means re-implementing those primitives in a
  JSON interpreter. Keep them in Rust.
- **The document format that earns its place is small**: a per-icon *hint*
  (which subpaths fill, mark radius factor, pigment role overrides), not a full
  artwork schema. Lucide supplies the geometry; the hint supplies the painting.
- **SVG as the general geometry source** buys nothing over Lucide for icons and
  cannot carry the painting phases the scenes need.

## What the Rust layer carries that a scene document cannot

`SceneDocumentV1` is an instantiated scene: pigment indices, final amounts, one
detail tier's events, one ground's omissions, sampled points, choreographed
spans. The parametric inputs are resolved away.

| Input | Consumed at | Declarable? |
|---|---|---|
| Palette roles to pigment indices | `role`, `granulating_role` (`authoring/mod.rs`) | yes: `role` per op |
| Intensity scaling | `Style::conc`, `Style::water`, `Style::glow` | yes: base amounts scaled at build |
| Detail gating | `Style::fine`, `Style::full`, `Style::ticks` | yes: `min_detail` per op |
| Dark-ground omissions | `Style::dark` (`icons.rs`, `accents.rs`) | yes: `light_only` per op |
| Seeded geometry streams | `Style::stream` (`accents.rs`, `scenes.rs`) | only by re-implementing the primitives |
| Aspect `Frame` mapping | `geometry.rs` | yes: design-space coordinates + `size_hint` |
| Choreography | `choreograph_scene` | yes: pure transform re-run at build |
| Sim resolution override | `Style::resolution` | already a document field |

Everything but the seeded geometry is a per-operation tag. The seeded geometry
is where the scenes live (`Crescent`, `Hills`/`ridge`, `WaterBody`, `bell_outline`,
per-seed wobble), so a lossless artwork document needs five procedural
primitives and two rules (`granulating_role`, glow dark amounts) in its
interpreter. That is the Rust authoring layer again, in JSON.

## Options

| Option | Designer without Rust | Runtime cost | Fits scenes | Fits icons | Cost |
|---|---|---|---|---|---|
| (a) Rust functions, as today | no | none | yes | yes, an hour each | 0 |
| (b) parametric artwork document + interpreter | yes, but only if documents load at runtime | none if compiled in; a parse if fetched | only with the primitives above | yes | interpreter 500-700 lines, 38 documents |
| (c) SVG geometry + painting side-car | yes | parser | no: the painting phases live in the side-car | yes | 800-1,200 lines, 38 re-authored |
| (d) Lucide builder for icons, Rust for scenes and accents | yes for icons | none | n/a | yes, zero authoring | built in this spike |

The one thing a document format genuinely buys is loading an artwork the wasm
was not built with. That matters for icons (any of Lucide's ~1,600) and not for
the nine scenes, which ship with the library. (d) delivers it for icons through
the Lucide element list itself, which is already a JSON document.

## The Lucide spike

### Mapping

`authoring/svg.rs` flattens the seven Lucide element kinds (`path`, `circle`,
`rect`, `line`, `ellipse`, `polyline`, `polygon`; path grammar `M L H V C S Q T
A Z` absolute and relative) into subpaths tagged closed or open, maps the 24
grid into `0.1..0.9` of the square frame, and paints:

- each **closed** subpath as a `stencil_body` in `BaseWash` (mask, hatch fill,
  glaze, clear), in sequence;
- each **open** subpath as one wet-on-dry `Shadow` brush stroke at the Lucide
  stroke half-width (`0.8/24`, `MARK_RADIUS_FACTOR` 1.0);
- an icon with **no closed subpath** lays its longest open subpath as a fat
  `BaseWash` stroke (2.5x) and the rest as marks;
- at `Small`, marks shorter than 0.10 are dropped and at most four are kept.

No crate dependency was added. `lucide` (vanilla, ISC) is a peerDependency of
`@nocturne/watercolour`; the host passes an `IconNode`. When neither WebGPU nor
a baked asset is available, an icon source falls back to the plain Lucide SVG.

### Results (seed 42, `slate`, light and dark, 48 px and 512 px)

Reviewed by a vision model in three batches, adjudicated by the orchestrator.

| Verdict | Icons |
|---|---|
| Pass as generated | `calendar`, `bell`, `phone`, `heart`, `scale`, `megaphone`, `server`, `rocket`, `book-open`, `database` |
| Pass with tuning | `clock` (hand too faint on dark), `cpu` (pins vanish at 48 px), `fingerprint` (too many marks at 48 px), `flag` (cutout closes at 48 px), `battery` (level bars vanish), `sprout` (leaf drawn as a loop, not filled), `syringe` (marks congest at 48 px), `key` (head needs a lifted hole) |
| Cannot work | none |

Two systematic failure modes, both fixable by a hint rather than by
hand-authoring:

1. **Silhouettes drawn as open paths do not fill.** Lucide's `database` draws the
   cylinder sides as one open path; the top ellipse fills, the body stays an
   outline. A hint `fill: [subpath indices]` closes the gap.
2. **Mark density at 48 px.** Line icons carry more marks than a watercolour
   icon can hold at icon size. A per-icon `small_marks` cap, or a thicker
   `mark_radius` factor, fixes `cpu`, `fingerprint`, `syringe`, `battery`.

### Cost

Scene build is under 0.1 ms per icon. Simulation and render cost match the
hand-authored icons at the same detail tier (both hatch a 256 grid at `Large`;
`render_lucide` reports 300-540 ms GPU sim + ~100 ms render at 512 px, the same
band as `render_catalogue`). Flattened outlines are 18-84 points, well inside
the mask rasteriser's comfort zone after the acceleration below.

The icon surface (parser, builder, wasm binding, hints) grew the unoptimised
wasm module from 790,895 B to 966,642 B raw (280,741 B to 339,648 B gzip), a
fifth. Neither of those builds ran `wasm-opt`, which the build script skips
when binaryen is not on `PATH`. With binaryen 132 installed (`npm install -g
binaryen`) the same module is 652,953 B raw / 274,570 B gzip, smaller than the
pre-icon unoptimised baseline.

## Performance: the mask rasteriser

`rasterize_mask_aspect` was `O(cells x points)`: distance to every outline
segment for every cell, plus an even-odd cast over every edge. It now walks each
segment only over its bounding box expanded by `feather + radius` (a cell
farther than that from every segment is `0`), and buckets the even-odd edges
per row. Output is bit-identical: FNV-1a snapshot tests over 64 synthetic masks
and over every `SetMask` in the catalogue at every detail and ground were
captured from the old code and pass on the new, and were shown to go red under a
deliberately wrong band.

| Case | Before | After |
|---|---|---|
| `Path` mask, 96 points, 384 grid | 22 ms | 0.55 ms |
| `Path` mask, 400 points, 384 grid | 116 ms | 1.7 ms |
| `Polygon` mask, 96 points, 384 grid | 37 ms | 12 ms |
| Whole catalogue, every `SetMask`, `Large` | 378 ms | 167 ms |
| Whole catalogue, every `SetMask`, `ExtraLarge` | 792 ms | 382 ms |

`Polygon` is bounded by the inside test; a scanline fill would take it to the
`Path` numbers if it ever matters.

### Hints, applied

The hint layer shipped (`IconHints` in `svg.rs`; `ICON_HINTS` in
`src/api/icon-hints.ts`, sourced from `scripts/icon-hints.json`; `hints` on the
icon request, caller fields winning over the built-ins). Nine icons carry a
hint. The vision re-review of those nine: `clock`, `database`, `flag`, `sprout`
fixed outright; `battery`, `cpu`, `fingerprint`, `syringe` improved at 128 px
and still soft at 48 px (mark congestion, tunable); `key` has its hole but its
bow has no teeth, and the hand-authored key stays the reference. Nothing
regressed.

### Baked

The twelve wishlist icons are in `scripts/bake-manifest.json` as
`lucide:<name>` entries and baked to `assets/lucide-<name>/<palette>[_dark]/`
(96 files, 4.6 MB, about 195 KB per set, in line with the hand-authored sets).
`IconRef` sources resolve to those assets with the same default-palette
fallback, so on a page without WebGPU a baked icon shows paint and only an
unbaked icon falls to the plain SVG. Two things for the branch owner to decide:

- `assets/` is 5.4 MB tracked, down from 20.3 MB when it was PNG: the set is
  now WebP, which suits artwork that is mostly soft alpha. The growth from 15
  to 38 hand-authored artworks on this branch is what made the format worth
  changing. If it is still too heavy for git, the cheap cuts are dropping the
  dark surface for line icons, a smaller strip edge, or strips only.
- The full bake regenerated the 90 pre-existing asset files as well, and their
  bytes differ from the commit because the engine on this branch has moved
  (choreography, settle, reconstruction) since they were baked. A bake that
  matches the shipped engine is the correct state, but it is a large binary
  diff; `git checkout -- <paths>` restores the committed ones if preferred.

## Recommendation and next steps

1. **Icons come from Lucide.** Done: the twelve wishlist icons are in the
   Playground and the plan to hand-author them is retired.
2. **Hint table.** Done: `scripts/icon-hints.json` feeds `ICON_HINTS` and the
   bake; hosts may override per field on the request. Remaining tuning is the
   48 px mark congestion on `battery`, `cpu`, `fingerprint`, `syringe`.
3. **Keep hand-authoring where it wins**: `key` and `phone` (hand versions
   clearly better), and every scene and accent.
4. **Do not build the artwork-document interpreter.** Revisit only if a native
   host needs to load scenes it was not built with.
5. **Bake.** Done for the twelve wishlist icons; see "Baked" above for the
   size decision.
6. **`wasm-opt`.** Done on this machine (binaryen 132 via npm); CI images need
   the same install or they ship the unshrunk module.
7. If the polygon inside test ever shows in a profile (`distant-mountains`
   carries nine masks, 77 ms at `ExtraLarge`), replace the per-cell even-odd
   cast with a scanline fill; the hash snapshot tests make that a safe change.

## Found along the way

- `tests/reveal_preserves_the_artwork` in the infra crate aborts with a native
  exit code partway through in **debug** builds on this machine and passes in
  release (7/7, 7-14 min). Run the infra suite with `--release`.
