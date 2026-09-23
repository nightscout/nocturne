import type { ArtworkId, DetailLevel, IconHints, IconNode, PaletteId, Surface } from '../types';
import { DEFAULT_INTENSITY } from '../types';
import { ICON_HINTS } from './icon-hints';
import { WatercolourError, toWatercolourError } from './errors';
import type { WasmModule } from './engine-host';

/** Version of the scene document this build reads; the engine validates everything past it. */
export const SCENE_DOCUMENT_VERSION = 1;

export interface ArtworkRef {
  id: ArtworkId;
  palette?: PaletteId;
  seed?: number;
  intensity?: number;
  surface?: Surface;
  detail?: DetailLevel;
}

export interface IconRef {
  icon: IconNode[];
  name: string;
  /** Overrides the library's built-in tuning for `name`, per field. */
  hints?: IconHints;
  palette?: PaletteId;
  seed?: number;
  intensity?: number;
  surface?: Surface;
  detail?: DetailLevel;
}

/** A ready scene document (JSON string) or a catalogue or icon reference the engine expands. */
/**
 * A scene authored by the caller. `sceneJson` is a finished document; `scene`
 * is authored inside the live lease, for a caller that needs the wasm module
 * (a palette or a paper from the catalogue) before it can write one.
 */
export type AuthoredScene = { sceneJson: string } | { scene: (module: WasmModule) => string };

export type SceneSource = ArtworkRef | IconRef | AuthoredScene;

export function authoredSceneJson(source: AuthoredScene, module: WasmModule): string {
  return 'sceneJson' in source ? source.sceneJson : source.scene(module);
}

/** Live-mode overrides derived from the canvas's backing store size. */
export interface SceneResolutionOverride {
  detail?: DetailLevel;
  /** Sim grid side; 0 keeps the detail's default. */
  simResolution?: number;
}

export function isArtworkRef(source: SceneSource): source is ArtworkRef {
  return typeof (source as ArtworkRef).id === 'string';
}

export function isIconRef(source: SceneSource): source is IconRef {
  return Array.isArray((source as IconRef).icon);
}

export interface SceneDocumentHeader {
  version: typeof SCENE_DOCUMENT_VERSION;
  id?: string;
}

/**
 * Checks only the version field; a newer document fails here with a typed
 * error instead of deep inside the engine's parser.
 */
export function parseSceneDocument(json: string): SceneDocumentHeader {
  let value: unknown;
  try {
    value = JSON.parse(json);
  } catch (error) {
    throw new WatercolourError('InvalidScene', `scene document is not JSON: ${error instanceof Error ? error.message : String(error)}`);
  }
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new WatercolourError('InvalidScene', 'scene document is not an object');
  }
  const record = value as Record<string, unknown>;
  if (typeof record.version !== 'number') {
    throw new WatercolourError('UnsupportedVersion', 'scene document has no version field');
  }
  if (record.version !== SCENE_DOCUMENT_VERSION) {
    throw new WatercolourError(
      'UnsupportedVersion',
      `scene document version ${record.version} not supported (this build reads ${SCENE_DOCUMENT_VERSION})`,
    );
  }
  return { version: SCENE_DOCUMENT_VERSION, id: typeof record.id === 'string' ? record.id : undefined };
}

/**
 * Directory a baked asset set lives under: `moonlight` for a light page,
 * `moonlight_dark` for the same palette composited for a dark one. This is
 * an asset key only; the engine takes palette and surface separately.
 */
export function paletteKey(palette: PaletteId = 'moonlight', surface: Surface = 'light'): string {
  return surface === 'dark' ? `${palette}_dark` : palette;
}

/**
 * The library's built-in tuning for `name` with the caller's `ref.hints`
 * layered over it; the caller wins per field. `undefined` when there is
 * nothing to send (no built-in entry, no caller hints).
 */
export function mergeIconHints(name: string, hints?: IconHints): IconHints | undefined {
  const builtin = ICON_HINTS[name];
  if (!builtin && !hints) return undefined;
  const caller: Record<string, unknown> = {};
  for (const key of Object.keys(hints ?? {})) {
    const value = (hints as Record<string, unknown>)[key];
    if (value !== undefined) caller[key] = value;
  }
  return { ...builtin, ...(caller as IconHints) };
}

export function resolveSceneJson(
  module: Pick<WasmModule, 'catalogueScene' | 'iconScene'>,
  ref: ArtworkRef | IconRef,
  override: SceneResolutionOverride = {},
): string {
  try {
    if (isIconRef(ref)) {
      const hints = mergeIconHints(ref.name, ref.hints);
      return module.iconScene(
        JSON.stringify(ref.icon),
        ref.name,
        ref.seed ?? 0,
        ref.palette ?? 'moonlight',
        ref.intensity ?? DEFAULT_INTENSITY,
        override.detail ?? ref.detail ?? 'large',
        ref.surface ?? 'light',
        override.simResolution ?? 0,
        hints ? JSON.stringify(hints) : '',
      );
    }
    return module.catalogueScene(
      ref.id,
      ref.seed ?? 0,
      ref.palette ?? 'moonlight',
      ref.intensity ?? DEFAULT_INTENSITY,
      override.detail ?? ref.detail ?? 'large',
      ref.surface ?? 'light',
      override.simResolution ?? 0,
    );
  } catch (error) {
    throw toWatercolourError(error);
  }
}

/** Stroke colour for the plain-SVG fallback, chosen so the stroke reads on the host ground. */
export function iconSvgStroke(surface: Surface): string {
  return surface === 'dark' ? '#e8e6e1' : '#3d4451';
}

/**
 * The plain Lucide SVG for an element list, used when live mode is unavailable
 * and no baked asset exists for an icon source.
 */
export function iconSvg(icon: IconNode[], surface: Surface): string {
  const parts = icon.map(([tag, attrs]) => {
    const a = Object.entries(attrs)
      .filter(([, v]) => v != null)
      .map(([k, v]) => `${k}="${String(v).replaceAll('"', '&quot;')}"`)
      .join(' ');
    return `<${tag} ${a}/>`;
  });
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="${iconSvgStroke(surface)}" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${parts.join('')}</svg>`;
}
