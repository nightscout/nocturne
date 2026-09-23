export type ArtworkId =
  | 'wash'
  | 'crescent-moon'
  | 'alarm-bell'
  | 'linked-rings'
  | 'report-pages'
  | 'magnifying-glass'
  | 'confirmation-mark'
  | 'moonlit-shoreline'
  | 'distant-mountains'
  | 'connected-shores'
  | 'overlapping-shapes'
  | 'avatar-wash'
  | 'tab-underline'
  | 'selection-edge'
  | 'confirmation-background'
  | 'header-motif'
  | 'calendar'
  | 'clock'
  | 'stopwatch'
  | 'sunrise'
  | 'footprints'
  | 'apple'
  | 'pizza-slice'
  | 'spanner'
  | 'suitcase'
  | 'paint-palette'
  | 'key'
  | 'plug'
  | 'apartment'
  | 'world-globe'
  | 'github-mark'
  | 'heart'
  | 'blood-drop'
  | 'heart-rate'
  | 'shield'
  | 'people-group'
  | 'exclamation-mark'
  | 'chat-bubble'
  | 'phone';

export type PaletteId = 'moonlight' | 'water' | 'dusk' | 'ember' | 'moss' | 'slate';

/**
 * A Lucide icon element, shaped to match the vanilla `lucide` package's
 * `IconNode`: a `[tag, attrs]` pair in a 24x24 stroke-drawn icon. The host
 * app's `IconNode` (e.g. `import { Clock } from 'lucide'`) is assignable to
 * `IconNode[]` here, so no import from the peer is needed for the type.
 */
export type IconNode = readonly [tag: string, attrs: Record<string, string | number | undefined>];

/** The palette roles a scene's pigments can take, as the Rust `PigmentRole`. */
export type PigmentRole = 'base_wash' | 'shadow' | 'accent' | 'glow';

/**
 * Per-icon tuning layered over the generic closed-subpath/open-subpath
 * mapping, keyed by Lucide name. Every field is optional; an absent one keeps
 * the library default. Serialised camelCase straight onto the `iconScene`
 * wire.
 */
export interface IconHints {
  /** Indices of open subpaths to close and paint as bodies (flattened order). */
  fill?: number[];
  /** Multiplier on the Lucide stroke half-width for marks. 1.0 = as drawn. */
  markRadius?: number;
  /** Marks kept at `DetailLevel.Small` (longest first). */
  smallMarks?: number;
  /** Circles lifted out of the wet body before it glazes, in 24-grid units: `[cx, cy, r]`. */
  holes?: [number, number, number][];
  bodyRole?: PigmentRole;
  markRole?: PigmentRole;
}

/**
 * An icon source for `Artwork`: the element list plus a name that becomes the
 * scene id (`lucide-<name>-<palette>-<seed>`). The host app supplies the
 * element list from `lucide` (e.g. `import { Clock } from 'lucide'`), whose
 * `IconNode` type matches this structurally. `hints` overrides the library's
 * built-in tuning per field.
 */
export interface IconArtworkSource {
  icon: IconNode[];
  name: string;
  hints?: IconHints;
}

export type ArtworkMode = 'auto' | 'live' | 'baked' | 'static';
export type ArtworkMotion = 'auto' | 'reduced' | 'full';
export type ArtworkQuality = 'auto' | 'low' | 'medium' | 'high';
export type ArtworkAutoplay = 'once' | 'never';

/**
 * How a component lays its canvas out in a container that is not the
 * artwork's own aspect. `contain` (default) sizes the canvas to the largest
 * box of the artwork's aspect that fits and centres it, leaving the
 * surrounding area transparent; `fill` stretches the artwork to the
 * container as the components did before aspect awareness.
 */
export type FitMode = 'contain' | 'fill';

/**
 * The page background the artwork sits on. `dark` keeps the palette and
 * switches the scene to luminous compositing (`Background::TransparentOnDark`).
 */
export type Surface = 'light' | 'dark';

/** How much of an artwork the catalogue draws for the size it is shown at. */
export type DetailLevel = 'small' | 'medium' | 'large' | 'extraLarge';

export interface ArtworkOptions {
  palette?: PaletteId;
  seed?: number;
  /** 0..1, default 0.7: scales pigment concentration. */
  intensity?: number;
  /** Wall-clock length of the whole reveal, default 3000. */
  durationMs?: number;
  /**
   * 0..1, default 0.8: the share of the wall clock spent setting into the
   * page, after the brushwork finishes. `tail = 0.8` of a 3000 ms reveal
   * means 600 ms of brushwork then 2400 ms of settling. It used to mean a
   * share of simulation ticks, and for baked reveals a hold on the finished
   * frame; both are gone — the settle phase is now baked at wall-clock
   * spacing.
   */
  tail?: number;
  /**
   * Maps wall-clock progress (0..1) to the progress shown, e.g.
   * `import { cubicOut } from 'svelte/easing'`. Absent = the engine's
   * built-in front-loaded curve.
   */
  easing?: (t: number) => number;
  motion?: ArtworkMotion;
  quality?: ArtworkQuality;
  mode?: ArtworkMode;
  autoplay?: ArtworkAutoplay;
}

export const ARTWORK_IDS: readonly ArtworkId[] = [
  'wash',
  'crescent-moon',
  'alarm-bell',
  'linked-rings',
  'report-pages',
  'magnifying-glass',
  'confirmation-mark',
  'moonlit-shoreline',
  'distant-mountains',
  'connected-shores',
  'overlapping-shapes',
  'avatar-wash',
  'tab-underline',
  'selection-edge',
  'confirmation-background',
  'header-motif',
  'calendar',
  'clock',
  'stopwatch',
  'sunrise',
  'footprints',
  'apple',
  'pizza-slice',
  'spanner',
  'suitcase',
  'paint-palette',
  'key',
  'plug',
  'apartment',
  'world-globe',
  'github-mark',
  'heart',
  'blood-drop',
  'heart-rate',
  'shield',
  'people-group',
  'exclamation-mark',
  'chat-bubble',
  'phone',
];

export const PALETTE_IDS: readonly PaletteId[] = ['moonlight', 'water', 'dusk', 'ember', 'moss', 'slate'];

export const DEFAULT_INTENSITY = 0.7;
export const DEFAULT_DURATION_MS = 3000;
export const DEFAULT_TAIL = 0.8;

/**
 * How long a mark takes to arrive, in ms.
 *
 * This is a hover state on a UI element, not a hero: it has to feel like a
 * response to the pointer. The engine's own 3 s reveal is for a piece of
 * artwork someone is looking at, and reads as a hang on a card.
 */
export const DEFAULT_REVEAL_MS = 420;
/** The settle runs past the spread, so the mark is still gaining pigment when it stops moving. */
export const REVEAL_SETTLE_RATIO = 1.45;

/**
 * The natural width/height ratio of each catalogue artwork, from the baked
 * aspect table (icons and `wash` are square, scenes and accents keep the
 * ratio they were authored at). The components use it to size the canvas in
 * `contain` mode and to pick the detail tier from the actual painted box.
 */
export const ARTWORK_ASPECT: Readonly<Record<ArtworkId, number>> = {
  wash: 1,
  'crescent-moon': 1,
  'alarm-bell': 1,
  'linked-rings': 1,
  'report-pages': 1,
  'magnifying-glass': 1,
  'confirmation-mark': 1,
  'moonlit-shoreline': 16 / 9,
  'distant-mountains': 2,
  'connected-shores': 2,
  'overlapping-shapes': 1,
  'avatar-wash': 1,
  'tab-underline': 8,
  'selection-edge': 1 / 6,
  'confirmation-background': 3,
  'header-motif': 5,
  'calendar': 1,
  'clock': 1,
  'stopwatch': 1,
  'sunrise': 1,
  'footprints': 1,
  'apple': 1,
  'pizza-slice': 1,
  'spanner': 1,
  'suitcase': 1,
  'paint-palette': 1,
  'key': 1,
  'plug': 1,
  'apartment': 1,
  'world-globe': 1,
  'github-mark': 1,
  'heart': 1,
  'blood-drop': 1,
  'heart-rate': 1,
  'shield': 1,
  'people-group': 1,
  'exclamation-mark': 1,
  'chat-bubble': 1,
  'phone': 1,
};

export function artworkAspect(id: ArtworkId): number {
  return ARTWORK_ASPECT[id] ?? 1;
}

/**
 * Detail tier for the canvas's BACKING long edge (CSS size times device
 * pixel ratio, capped at 2). Mirrors `DetailLevel::detail_for_edge_px` in
 * the wasm crate: below 64 px small, below 192 px medium, below 320 px
 * large, 320 px and above extraLarge.
 */
export function detailForEdge(edgePx: number): DetailLevel {
  if (edgePx < 64) return 'small';
  if (edgePx < 192) return 'medium';
  if (edgePx < 320) return 'large';
  return 'extraLarge';
}

/** FNV-1a over the name, so the same person always receives the same wash. */
export function seedFromName(name: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < name.length; i++) {
    h ^= name.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h;
}
