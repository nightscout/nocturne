export { default as Artwork } from './components/Artwork.svelte';
export { default as ArtworkHero } from './components/ArtworkHero.svelte';
export { default as PaintedUnderline } from './components/PaintedUnderline.svelte';
export { default as SelectionEdge } from './components/SelectionEdge.svelte';
export { default as AvatarWash } from './components/AvatarWash.svelte';
export { default as ConfirmationBackground } from './components/ConfirmationBackground.svelte';
export { default as HeaderMotif } from './components/HeaderMotif.svelte';
export { default as DropSurface } from './components/DropSurface.svelte';
export { default as DropGroup } from './components/DropGroup.svelte';
export { createDropGroup, getDropGroup, setDropGroup } from './components/drop-group';
export type { DropGroupContext } from './components/drop-group';
export type { PlayerReadyCallback } from './components/helpers';
export { hostSurface, watchSurface } from './components/helpers';

export type {
  ArtworkId,
  PaletteId,
  ArtworkOptions,
  ArtworkMode,
  ArtworkMotion,
  ArtworkQuality,
  ArtworkAutoplay,
  FitMode,
  Surface,
  DetailLevel,
  IconNode,
  IconHints,
  IconArtworkSource,
  PigmentRole,
} from './types';
export { ARTWORK_IDS, PALETTE_IDS, DEFAULT_INTENSITY, DEFAULT_DURATION_MS, DEFAULT_TAIL, DEFAULT_REVEAL_MS, REVEAL_SETTLE_RATIO, ARTWORK_ASPECT, artworkAspect, seedFromName, detailForEdge } from './types';

export { createArtworkPlayer } from './api/playback';
export type { ArtworkPlayer, PlayerOptions, PlayerState, PlayerEvent, FallbackDetail } from './api/playback';

export { catalogueIds } from './api/catalogue';

export { detectCapabilities, probeCapabilities, resetCapabilitiesCache, prefersReducedMotion } from './api/capabilities';
export type { Capabilities, CapabilityEnvironment } from './api/capabilities';

export { EngineHost, getEngineHost, configureEngineHost, DEFAULT_MAX_LIVE_INSTANCES } from './api/engine-host';
export type { EngineHostOptions, EngineLease, EngineStats, WasmModule } from './api/engine-host';

export { Scheduler, getScheduler, MAX_FRAME_SECONDS } from './api/scheduler';
export type { SchedulerEnv, SchedulerHandle, SchedulerTarget, FrameStats } from './api/scheduler';

export { resolveMode, resolveMotion, fallbackOrder } from './api/mode';
export type { ResolvedMode, ModeInputs } from './api/mode';

export {
  parseBakedManifest,
  stripFramePosition,
  loadStrip,
  drawStripFrame,
  loadStill,
  drawStill,
  BAKED_MANIFEST_VERSION,
  MAX_BAKED_FRAMES,
  MAX_BAKED_FRAME_EDGE,
  MAX_BAKED_UPSCALE,
  bakedServesEdge,
} from './api/baked';
export type { BakedManifest, StripPosition, StripBitmap } from './api/baked';

export { parseSceneDocument, resolveSceneJson, paletteKey, isArtworkRef, mergeIconHints, iconSvg, SCENE_DOCUMENT_VERSION } from './api/scenes';
export type { ArtworkRef, SceneSource, AuthoredScene, SceneDocumentHeader } from './api/scenes';
export { authoredSceneJson } from './api/scenes';

export { ICON_HINTS } from './api/icon-hints';

export { assetUrl, assetAvailable, hasBundledAsset, loadManifest, bundledArtworkIds, defaultPaletteFor, DEFAULT_PALETTE } from './api/assets';
export type { AssetVariant, AssetOptions, AssetKey } from './api/assets';

export { fitMark, fitStroke, fitDrops, fitSplotch, borderMark, washStroke, glazeStroke, mergeReserves, clearanceField, strokeStep, bleedFor, MAX_THICKNESS, MAX_RADIUS, MAX_DROP_RADIUS } from './api/drop-stroke';
export type { DropMark, DropKind, MarkOptions, DropStroke, StrokeOptions, StrokeSide, ClearanceField, Dab, Pt } from './api/drop-stroke';
export { dropScene, strokeFrame } from './api/drop-scene';
export type { DropScene, DropSceneOptions, DropDeposit } from './api/drop-scene';

export { measureObstacles, textBoxes, DROP_TEXT_PAD } from './api/drop-text';
export type { DropFont, DropFonts } from './api/drop-text';

export { PALETTE_PIGMENTS, surfaceTint, SURFACE_TINT_ALPHA } from './api/drop-colour';
export type { PigmentColours } from './api/drop-colour';

export { WatercolourError, toWatercolourError } from './api/errors';
export type { WatercolourErrorCode } from './api/errors';
