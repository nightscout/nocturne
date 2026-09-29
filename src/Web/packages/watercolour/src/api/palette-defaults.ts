import type { ArtworkId, PaletteId } from '../types';

/**
 * The palette each artwork ships baked with (`scripts/bake-manifest.json`).
 * The bundle carries one set per artwork, so any other palette falls back to
 * this one's assets rather than missing entirely.
 */
export const DEFAULT_PALETTE: Record<string, PaletteId> = {
  'crescent-moon': 'moonlight',
  'alarm-bell': 'moonlight',
  'linked-rings': 'dusk',
  'report-pages': 'slate',
  'magnifying-glass': 'water',
  'confirmation-mark': 'moss',
  'avatar-wash': 'water',
  'tab-underline': 'ember',
  'selection-edge': 'water',
  'confirmation-background': 'moss',
  'header-motif': 'moonlight',
  'moonlit-shoreline': 'moonlight',
  'distant-mountains': 'slate',
  'connected-shores': 'water',
  'overlapping-shapes': 'dusk',
  'calendar': 'moonlight',
  'clock': 'slate',
  'stopwatch': 'slate',
  'sunrise': 'ember',
  'footprints': 'moss',
  'apple': 'moss',
  'pizza-slice': 'ember',
  'spanner': 'slate',
  'suitcase': 'dusk',
  'paint-palette': 'dusk',
  'key': 'ember',
  'plug': 'slate',
  'apartment': 'slate',
  'world-globe': 'water',
  'github-mark': 'slate',
  'heart': 'ember',
  'blood-drop': 'ember',
  'heart-rate': 'ember',
  'shield': 'water',
  'people-group': 'dusk',
  'exclamation-mark': 'ember',
  'chat-bubble': 'water',
  'phone': 'slate',
  'cgm-sensor': 'water',
  'insulin-pump': 'slate',
  'glucose-gauge': 'slate',
  'ringing-bell': 'ember',
  'hub-dawn-ridges': 'dusk',
  'lucide-database': 'slate',
  'lucide-server': 'slate',
  'lucide-cpu': 'slate',
  'lucide-fingerprint': 'water',
  'lucide-battery': 'water',
  'lucide-sprout': 'moss',
  'lucide-scale': 'moss',
  'lucide-syringe': 'ember',
  'lucide-flag': 'ember',
  'lucide-megaphone': 'ember',
  'lucide-rocket': 'ember',
  'lucide-book-open': 'moonlight',
};

export function defaultPaletteFor(id: ArtworkId | string): PaletteId {
  return DEFAULT_PALETTE[id] ?? 'moonlight';
}

/** The bundled asset id of a baked Lucide icon: `assets/lucide-<name>/...`. */
export function iconAssetId(name: string): string {
  return `lucide-${name}`;
}

/**
 * The palette a source paints in: its own, else the one its artwork is baked
 * in. The live rung and the asset lookup both resolve through here, so a
 * source with no palette paints the same colours live as baked.
 */
export function sourcePalette(id: ArtworkId | string, palette?: PaletteId): PaletteId {
  return palette ?? defaultPaletteFor(id);
}
