# Public API

Everything is exported from `@nocturne/watercolour` (`src/Web/packages/watercolour/src/index.ts`).
The public surface is plain TypeScript; the Svelte components are thin wrappers
over it.

## `createArtworkPlayer`

The core entry point (`src/api/playback.ts`). Builds a player that draws one
artwork to a canvas, choosing the best backend it can.

```ts
const player = createArtworkPlayer(
  canvas,
  { id: 'crescent-moon', palette: 'moonlight', seed: 42, intensity: 0.7, surface: 'dark' },
  { durationMs: 3000, mode: 'auto', motion: 'auto', autoplay: 'once', width: 256, height: 256, dpr: window.devicePixelRatio },
);
```

The second argument is a `SceneSource`: an `ArtworkRef` (`{ id, palette?, seed?,
intensity?, surface?, detail? }`) that the engine expands via `catalogueScene`,
an `IconRef` (`{ icon, name, hints?, ... }`) whose element list `iconScene`
authors into a `lucide-<name>` scene (see Icon sources below), or
`{ sceneJson }` / a JSON string holding a versioned scene document (only the
`version` field is checked here; the engine validates the rest).

`surface: 'light' | 'dark'` is the page background the artwork sits on. `dark`
keeps the chosen palette and switches the scene to luminous compositing
(`Background::TransparentOnDark`); it does **not** select the `<palette>_dark`
palettes, which are subtractive variants for hosts that composite over a dark
ground themselves.

### `durationMs` and `tail`

`durationMs` (default `DEFAULT_DURATION_MS` = 3000) is the whole wall-clock
length of the reveal. `tail` (default `DEFAULT_TAIL` = 0.8) is the **share of
that wall clock spent setting into the page** after the brushwork finishes:
`tail = 0.8` at `durationMs = 3000` means 600 ms of brushwork then 2400 ms of
settling. In live mode it maps to the engine's `Reveal` curve as
`paintWallFraction = 1 - tail`, and in baked mode the strip is already baked at
wall-clock spacing, so the tail plays straight through the strip's settling
frames. `tail` used to mean a share of simulation ticks, and for baked reveals
a hold on the finished frame; both are gone.

`checkpointBudgetBytes` (live only) is the GPU memory the instance may spend on
seek checkpoints. Absent keeps the engine's 48 MB default; `0` leaves it the
single checkpoint at tick 0, so a backwards `seek` reloads and replays from the
start rather than restoring a nearer state. A player nothing ever seeks - every
drop outside the showcase scrubber - should pass it.

### Player methods and state

```ts
player.play(); player.pause(); player.reset(); player.seek(0.5); player.finishImmediately();
player.resize(width, height, dpr);
const png = await player.exportPng(512, 512);            // live only
const { strip, manifest } = await player.exportStrip(12, 256);  // live only
player.dispose();
```

`state` is a snapshot (`mode`, `motion`, `playing`, `finished`, `progress`,
`error`, `fallbackReason`); it is never pushed per frame. Events:

| Event | Payload | Fires |
|---|---|---|
| `ready` | - | a backend is drawing (or the player settled on `none`) |
| `finished` | - | the reveal completed |
| `fallback` | `{ from, error }` | a backend failed; a lower one took over |
| `error` | `WatercolourError` (typed `code`) | nothing could draw |
| `statechange` | - | any state change |

`player.ready` resolves once a backend is drawing. `player.canvas` is the
current element, which differs from the one passed in only after a live-to-baked
fallback (a WebGPU canvas can never give a 2D context, so the element is
replaced in place).

## `detectCapabilities`

`src/api/capabilities.ts`. Probed once per page (cacheable):

```ts
const caps = await detectCapabilities(); // { webgpu, adapter, reducedMotion, offscreenCanvas, reason? }
```

Chrome exposes `navigator.gpu` on machines with no usable adapter, so
`webgpu` and `adapter` are reported separately, with `reason` explaining why
live mode is unavailable when it is.

## Engine host

`src/api/engine-host.ts`. **One WebGPU device per page**, with compiled
pipelines shared by every instance (`GpuEngine::fork`). `release` never tears
the engine down: the compiled pipelines cost more to rebuild than to keep.

```ts
import { getEngineHost, configureEngineHost } from '@nocturne/watercolour';
const host = getEngineHost();
host.stats();                 // { liveInstances, maxLiveInstances, checkpointBytes, lastStepMs, lastRenderMs, initMs, adapterName }
host.maxLiveInstances = 4;    // or configureEngineHost({ maxLiveInstances })
host.onLost((message) => {}); // device lost; players fall back
```

`configureEngineHost` replaces the page singleton and must be called before the
first artwork mounts. The wasm bindings are loaded through a Vite glob because
`src/wasm/` is gitignored; when they are not built the host reports
`EngineUnavailable` and every player answers with baked/static.

## Scheduler

`src/api/scheduler.ts`. **One `requestAnimationFrame` loop** for every live or
baked instance on the page. Hidden time is not counted as elapsed, so a reveal
resumes where it paused instead of jumping to the end. Off-screen artworks are
neither stepped nor rendered (`IntersectionObserver`); a stalled frame is
clamped to `MAX_FRAME_SECONDS = 0.25`.

```ts
import { getScheduler } from '@nocturne/watercolour';
getScheduler().stats(); // { frames, averageMs, p95Ms, lastMs } frame-time histogram
```

## Assets

`src/api/assets.ts`. Bundled assets live under `assets/<artwork>/<paletteKey>/<file>`
and are resolved by Vite at build time; `assetBaseUrl` serves them from a
different origin; explicit `assets` URLs override both. Only the default palette
per artwork is bundled; a missing palette falls back to it (see
[scene-format.md](scene-format.md)).

```ts
import { assetUrl, defaultPaletteFor, hasBundledAsset } from '@nocturne/watercolour';
```

## Icon sources

`Artwork` also renders a Lucide icon as a watercolour scene. The `icon` prop
takes an `IconArtworkSource` (`{ icon, name, hints? }`): `icon` is the element
list, `name` becomes the scene id (`lucide-<name>-<palette>-<seed>`), and
`hints` tunes the mapping per field. It takes precedence over `artwork`. The
host supplies the element list from the vanilla `lucide` package, which is a
**peerDependency** (`>=0.400.0`, ISC) - the library never imports it:

```svelte
<script lang="ts">
  import { Database } from 'lucide';
</script>
<Artwork icon={{ icon: Database, name: 'database' }} />
```

`lucide`'s `IconNode` type matches the library's `IconNode` structurally, so
the import needs no cast. The library ships a built-in tuning table,
`ICON_HINTS` (from `scripts/icon-hints.json`), applied per name before the
element list reaches `iconScene`; `mergeIconHints(name, callerHints)` layers a
caller's hints over it, caller wins per field.

Icon sources resolve assets like any artwork (baked `lucide-<name>` set when
one exists, default-palette fallback included). When neither WebGPU nor a baked
asset is available, an icon falls to the **plain Lucide SVG** (`iconSvg`) at the
`static` rung instead of the finished PNG - it always resolves, so an icon
never falls to `none`.

## The Svelte components

All components are Svelte 5 runes, decorative (`aria-hidden`, `role="presentation"`),
size their canvas to the container via `ResizeObserver` (DPR capped at 2), create
the player in an effect and dispose it on destroy or when any prop changes.
Every component accepts the `ArtworkOptions` props (`palette`, `seed`,
`intensity`, `durationMs`, `motion`, `quality`, `mode`, `autoplay`), an
optional `surface`, a `fit` prop, an `onready` callback, and `class`.

Each artwork has a natural aspect (`ARTWORK_ASPECT` / `artworkAspect(id)`; the
icons and `wash` are square, the scenes and accents keep their authored ratio).
With `fit="contain"` (default) the canvas is the largest box of that aspect
inside the container, centred, and the surrounding area stays transparent;
`fit="fill"` stretches to the container as the components did before aspect
awareness. `fit` may also be a function of the container size -
`ConfirmationBackground` uses that to fill only near its 3:1 aspect. `onready`
fires once a backend is drawing; the returned cleanup runs with the player's
disposal.

| Component | Artwork id it renders | Extra props | Notes |
|---|---|---|---|
| `Artwork` | the `artwork` prop, or the `icon` prop (a Lucide element list, takes precedence) | `artwork: ArtworkId`, `icon?: IconArtworkSource`, `assetBaseUrl` | Renders `detailForEdge` from its rendered box's backing long edge (below 64 px small, below 192 px medium, below 320 px large, 320 px and above extraLarge; sim grids 96/160/256/384, the tier's own whatever the canvas size); `surface` defaults from the host theme: a `.dark`/`.light` class on `<html>`, then `<html>`'s computed `color-scheme`, then `prefers-color-scheme`. |
| `PaintedUnderline` | `tab-underline` | `active: boolean` | `opacity-0` unless `active`; plays once on activation. A 2px hairline in a tab row. |
| `SelectionEdge` | `selection-edge` | `active: boolean`, `side: 'left' \| 'top'` | A vertical or horizontal edge strip; plays once on activation. |
| `AvatarWash` | `avatar-wash` | `name: string`, `size = 32` | Seed derives from `name` via `seedFromName` unless given. Defaults to `auto` mode with `releaseAfterFinish`, so each head paints one frame live and releases the engine (the canvas keeps the pixels) - a member list holds dozens of avatars and a live slot per head would exhaust the cap. |
| `ConfirmationBackground` | `confirmation-background` | - | Fills its container only when it is within 20% of the artwork's 3:1 aspect, else `contain` anchored bottom-left. On dark surfaces the canvas runs at CSS opacity 0.45 because Luminous alpha saturates. |
| `HeaderMotif` | `header-motif` | - | Fixed `aspect-ratio: 5/1; width: 10rem` (160x32); plays once. |
| `DropSurface` | a stroke generated for the surface (`fitStroke`, `dropScene`) | see [Paint drops](#paint-drops) | Wraps arbitrary content and paints one brush stroke in its empty space on hover, selection or focus. Live only. |
| `DropGroup` | - | `name?: string` | Hands each `DropSurface` inside it an index and a shared seed, so a run varies by seed. |

```svelte
<Artwork artwork="crescent-moon" palette="moonlight" seed={42} surface="dark" class="size-32" />
<HeaderMotif palette={settings.accentPalette} seed={settings.artworkSeed} class="hidden sm:flex" />
```

## Paint drops

`DropSurface` is the one component that generates artwork rather than rendering a
fixed piece of it. A card, a row or a button shows content; on hover the surface
paints **one gesture** in the space around that content, never over it: a tapered
brush stroke, two or three round drops, one splotch spilling off the top and bottom,
a thin border round the whole card, or a wash round the copy. A button, which is
all label, takes a glaze instead: the one gesture laid over its content. It is
a touch of magic, so it is rare: a surface earns a stroke only when the user points
at it on purpose, there is genuinely empty space beside the copy, and it is a moment
rather than a dense working surface. Charts, tables, forms and anything read for
numbers get nothing.

### The stack

A surface paints its own background, so a mark with a negative z-index disappears
behind the background it is meant to bleed into. The component is three layers and
the host has to keep them apart:

- the host takes `class` - border, background, radius, and `overflow: hidden`;
- the stroke's canvas sits in an `inset-0` layer above that background;
- the content takes `contentClass` and sits above the canvas.

### How the stroke is found

`fitStroke` (`src/api/drop-stroke.ts`) works from a **clearance field**: for every
grid point on the surface and a bleed margin past its edges, the distance to the
nearest piece of content. Text and icons are obstacles; the paper past the edge is
open, so a stroke may run off the surface.

1. **Corridor.** Straight chords are swept across the field at eight angles, cut
   into runs of open samples, and scored by the visible brush width they could
   carry. A brush that crosses a surface edge scores higher than one that floats:
   marks flush to an edge have never failed a review, free discs did. The best run
   per side of the surface is kept, and near-ties (within 12 %) are interchangeable,
   so the seed decides *where* a stroke goes but never *whether* one is drawn - a run
   of identical cards either all draw or none do.
2. **Gesture.** The stroke takes 55-80 % of its corridor, bows by 8-18 % of its
   length toward the roomier side, and tapers from a fat end to a thin one at
   22-40 % of its width. The fat end goes where there is more room.
3. **Size.** The brush is bounded by the surface's short edge (`MAX_THICKNESS`, 42 %)
   **and** by an absolute width (`MAX_RADIUS`, 24 px): a brush is a physical thing,
   and a 330 px square given 42 % of its edge got a wave, not a stroke. The ink
   footprint is held under 20 % of the surface, shortened before thinned, and never
   below three widths long. A stroke that would be mostly off the surface is not
   drawn at all.
4. **Spatter.** Where there is room, a handful of small droplets around the fat end,
   smaller with distance, each on clear paper.

**Drops** (`fitDrops`) are the other gesture: two or three round dabs, largest first,
each taking the roomiest point left in the field, valued the same way as a corridor
sample so they sit against an edge or a corner before they float. The sizes step
down and the drops keep two and a half radii apart, so they read as one splash
rather than a row of coins. A drop is capped at `MAX_DROP_RADIUS` (20 px) and keeps
60 % of itself on the surface. Fewer than two fitting hands over to a stroke.

**Splotch** (`fitSplotch`) is one big circle filling the clear band beside the copy.
Its radius is `SPLOTCH_RADIUS` (0.9) of the surface height, so it leaves the paper
above and below and usually past the side too; what stays in view is the curve
toward the copy, a bracket of paint holding the text. It takes whichever side of
the copy is clearer, needs a band at least 24 px wide, gives up when nothing is in
the way (it would flood the surface), and is laid at 70 % concentration. Its
stamp's soft edge is held at the largest stroke brush's width rather than a share
of its radius: at a stroke's softness a 300 px circle faded over 200 px and dried
to a mist with no edge.

**Border** (`borderMark`) is a thin stroke round the perimeter, drawn like a frame:
it starts somewhere along the top, runs clockwise with a hand's wobble round
softened corners, thins as it goes and stops at 94 % of the way, so the gap says
drawn rather than printed. Its centreline runs just outside the edge, so the brush
overlaps the card and bleeds off it while the dried edge shows on the inside. It
fits any surface, so it is always available.

**Wash** (`washStroke`) is one brush wider than the surface is tall, run off both
ends, so the dried edge falls outside. The copy is **reserved**:
its measured boxes are merged into blocks (`mergeReserves`: boxes closer than
`RESERVE_MERGE_GAP`, 16 px, join, chains included, and a block that comes that
close to the surface's edge runs out past it), and two ticks after the wash lands
each block is lifted back out as a stack of capsules. A lifted cell dries, and the
flow does not cross into a dry cell, so the paint pools against the reserve's edge
and never sits under a word. It is laid at 55 % of the concentration.

**Glaze** (`glazeStroke`) is the same brush with nothing reserved, for a control
that is all label: a button has no open paper for a wash to keep to, so the paint
fills it whole and the label reads through. It is laid at 45 % of the
concentration, and it is only ever asked for (`kind="glaze"`), never cycled.

`fitMark` chooses between them. With `kind="auto"` a run **cycles** stroke, drops,
splotch and border by the member's turn, and the turn also rotates through the tied
corridors, so consecutive cards differ in gesture and in edge by rule rather than by
chance. A gesture that does not fit hands over to a stroke, then to drops, and only
when nothing fits is nothing drawn.

The catalogue marks this replaced were sized from the surface's short edge, so a
squarish card got a disc wider than itself - the three surfaces on which that
blobbed (245x205, 330x330, 403x142) are fixtures on the showcase page.

### How the stroke is painted

The stroke is a **scene** (`dropScene`, `src/api/drop-scene.ts`), authored in
TypeScript and handed to the engine as a raw document. It borrows the paper and
palette of a catalogue artwork (`avatar-wash`) and replaces only the timeline, so a
drop matches every other mark in the palette without a second source of pigment
data. The palette is trimmed to the pigments the drop actually lays down - one for
a stamp, two for a wet deposit - since every simulation cell carries state per
pigment. The document is authored inside the live lease through the player's
`{ scene: (module) => string }` source, because the palette comes from the wasm
module.

A **stroke** and a **border** are drawn: the pen travels the path, laying it in
spans of about 40 px (at most 12), one a tick, each span keeping the whole stroke's
radius profile so the dried mark is the one laid whole. Laid whole at the first
tick, a row-wide stroke read as a stamp dropped on the row. The pen moves at about
3 px/ms, and the host gives that time to the reveal's brushwork (`brushworkMs`),
at least the default 20 % and at most 50 % of the clock, so the ink still has time
to settle. Drops, a splotch, a wash and a glaze are laid down whole. Drops land one
after another, largest first, three ticks apart (about 45 ms at the default
reveal): drops that land on the same tick read as one stamp. Spatter is a single
flick and lands with the stroke.

| `deposit` | What it does |
|---|---|
| `wet` (default) | Charges the paper with water along the path first, drops the pigment into it, and adds a darker drop of the shadow pigment at the head. Blooms with a soft edge and granulates. |
| `stamp` | The pigment stroke alone. Flatter; kept for comparison on the showcase scrubber. |

Droplets are flicked, not washed: little water and higher concentration, so they
dry with an edge.

### Live only

A generated stroke has no baked asset, so there is no strip and no still. The
player is asked for `live` outright, and where it cannot have it - no WebGPU, or the
page is already at its cap of `DEFAULT_MAX_LIVE_INSTANCES` (4) - the surface shows
its ordinary hover state and **no mark**. That is the intended behaviour, not an
error; the old catalogue fallback was judged not worth keeping alongside. One
surface is one instance, whatever its size, and hover rarely holds more than one
open. A host that wants the marks calls `getEngineHost().warm()` at idle; without
it the first pointer pays the engine boot inside its own transition.

The canvas is only as large as the mark needs (`strokeFrame`): the paint plus room
for the bloom, clipped to the bleed the surface allows, and never more elongated
than 3:1, because the square simulation grid is stretched to the canvas and a long
frame has cells several times wider than they are tall, which shows as blocks along
a thin edge.

The grid itself is sized for a drop, not a hero (`dropSimResolution`):
one cell per device pixel of the canvas's long edge, capped at 512 rather than the
256 a hero tier uses. A drop's edge is the brush stamp itself, rasterised on
the grid, and at 256 over a card-wide canvas a hairline border was two cells thick
and pixelated.

### Measuring the content

| Prop | What it does |
|---|---|
| `fonts?: DropFonts` | A `DropFont` (`font`, `lineHeight`, `align?`) per `data-drop-text` value. Text is then laid out **off the DOM** by Pretext, which returns real line boxes with no layout read. |
| `data-drop-text="<key>"` | On a text element, names the font it is set in. |
| `data-drop-obstacle` | On anything that is not text - an icon, an avatar, an image - measured as one box. |

A `DropFont`'s `font` string has to match the host's CSS **exactly**, and its `align`
has to match the text's alignment: Pretext measures off that description, so
centred copy declared without `align: 'center'` returns line boxes on the left and
the stroke is fitted to space that is not empty. Text with no declared font falls
back to `Range.getClientRects`, which is correct and costs a layout read per text
node.

### Runs

Wrap a grid or a list in `DropGroup`. Each `DropSurface` claims its index at init,
and the group's seed (from its `name`) is folded into every member's. The member's
**turn** (its index plus a small offset from the group seed) decides its gesture and
which tied corridor it takes; the seed decides bow, taper, length and spatter. Two
runs on one page start at different turns, so they do not mirror each other. There
is no bookkeeping of what a neighbour drew.

### How a stroke arrives and leaves

The engine runs the reveal. The canvas is held **invisible** until the player has
reported `live`, then fades to `peak` over 160 ms; a machine that cannot go live
never shows it. Leaving fades from where the paint dried over `exitMs` (180 ms), with
the canvas kept mounted through the fade. Under reduced motion an explicit live
request stays live and the player shows the settled frame at once.

`revealMs` (420 ms by default) is the hover clock, with the settle running 1.45x
that; the engine's 3 s default reads as a hang on a card. `progress` pins the stroke
at a point of its settle with the player paused, which is how the two deposits are
compared at the same instant on the showcase scrubber.

### Colour

A live stroke takes `palette` directly (default `water`), which is exact. On a dark
surface the scene switches to luminous compositing, whose alpha saturates a dense
deposit: the stroke that is a translucent wash on white would dry to an opaque pale
bar on black. A dark stroke is therefore laid at under half the concentration, so
its alpha sits in the curve's ramp and carries the paper's texture, and the canvas
runs at 45 % of its opacity (`DARK_PEAK`). Both were judged by eye against the same
stroke on both grounds.

`tintSurface` warms the host toward the paint that landed on it, from
`PALETTE_PIGMENTS` - the `r_white` reflectances of the engine's own pigments, so
nothing reads a pixel back.

### Props

| Prop | Default | What it does |
|---|---|---|
| `as` | `div` | The tag the surface renders as. A surface has to BE the link or button it decorates. |
| `name` | `''` | Seeds the stroke, so one surface paints the same way across renders. |
| `trigger` | `hover` | `hover`, `select` (with `selected`), `focus` or `always`. |
| `shown` | | Overrides the trigger outright. |
| `fonts` | | Off-DOM measurement, above. |
| `palette` | `water` | The pigment palette. |
| `deposit` | `wet` | How the stroke is laid down, above. |
| `kind` | `auto` | `stroke`, `drops`, `splotch`, `border`, `wash`, `glaze`, or `auto` to cycle the first four along a run. `glaze` is for a control that is all label. |
| `spatter` | `true` | Droplets around the fat end where there is room. |
| `intensity` | `0.7` | Scales pigment concentration. |
| `peak` | `1` | Scales the canvas opacity. |
| `tintSurface` | `false` | Warms the host toward the paint. |
| `progress` | | Pins the stroke at a point of its settle. |
| `revealMs`, `exitMs` | `420`, `180` | The hover clock and the exit fade. |
| `generation` | `0` | Bumped to repaint with a fresh seed. |
| `onresolved` | | Reports the backend the player settled on, for a debug panel. |

### Example

```svelte
<DropGroup name="feature cards">
  {#each features as feature (feature.title)}
    <DropSurface
      name={feature.title}
      fonts={{ title: { font: '600 14px "Cabin", sans-serif', lineHeight: 20 },
               copy: { font: '400 14px "Cabin", sans-serif', lineHeight: 20 } }}
      class="rounded-xl border bg-card"
      contentClass="flex items-start gap-3 p-4"
    >
      <div data-drop-obstacle class="size-10"><Icon /></div>
      <div>
        <h3 data-drop-text="title" class="text-sm font-semibold">{feature.title}</h3>
        <p data-drop-text="copy" class="text-sm text-muted-foreground">{feature.copy}</p>
      </div>
    </DropSurface>
  {/each}
</DropGroup>
```

### Touch

A tap fires `pointerenter`, `pointerdown`, `pointerup` and **`pointerleave`**
inside the one gesture, so a hover trigger lit and went dark before anything could
be seen. A surface opened by a pointer that cannot hover keeps its stroke up, and
the next pointer that lands outside it takes it down. A mouse is unchanged.

### Off screen

A surface watches its own visibility. Until it comes within 250 px of the viewport
it fits no stroke and mounts no canvas; when it leaves again it gives both back. A
fit is **memoised** on what it was made from - the box, the props, the theme and
the fonts (compared by identity, so pass a constant) - and a resize settles for
120 ms before the stroke is fitted again.

### What it costs

The fit runs per surface on mount and again on resize. Measured in Node on the
current fitter, per call:

| Surface | Per fit |
|---|---|
| list row 420x62 | 0.49 ms |
| feature card 364x101 | 0.38 ms |
| wide card 900x180 | 0.43 ms |
| dense card 600x400, 12 lines | 0.57 ms |
| square card 330x330 | 0.20 ms |

Two things keep that number down: the field's step scales with the surface
(`strokeStep`), and the corridor sweep runs at 1.5 times that step and eight
angles, because it only has to find the corridor while the radius is fitted
against the finer field afterwards.

The live instance costs what any live artwork costs: a device, 50-90 ms to create,
and a simulation grid sized from the canvas's backing long edge (capped at 512 for a
drop, about four times the tick cost of 256, affordable because a drop holds one
checkpoint and two pigments). One surface is one instance, and it runs for well
under a second. A drop is never seeked unless the showcase scrubber pins it with
`progress`, so it takes `checkpointBudgetBytes: 1` and holds the single checkpoint
at tick 0. Measured on the showcase with sixteen surfaces held open at 512, that is
about 7 MB of checkpoint memory per instance against 25 MB before.

`/drops` in the showcase is the working reference: the two deposits side by side on
a scrubber, the feature cards, buttons and rows, and the three surfaces that blobbed
in both themes. The page raises the live cap so every surface can be held open at
once; production keeps the cap of four.
