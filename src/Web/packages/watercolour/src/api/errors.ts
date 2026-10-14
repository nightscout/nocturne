/**
 * Codes the wasm adapter prefixes onto every message (`Code: detail`), plus
 * the ones this layer raises itself.
 */
export type WatercolourErrorCode =
  | 'WebGpuUnavailable'
  | 'EngineUnavailable'
  | 'InitFailed'
  | 'InstanceLimit'
  | 'DeviceLost'
  | 'NoSurface'
  | 'InvalidScene'
  | 'InvalidDetail'
  | 'InvalidSurface'
  | 'UnknownArtwork'
  | 'UnknownPalette'
  | 'InvalidPalette'
  | 'InvalidStrip'
  | 'InvalidManifest'
  | 'AssetMissing'
  | 'UnsupportedVersion'
  | 'Disposed'
  | 'Engine'
  | 'Unknown';

export class WatercolourError extends Error {
  readonly code: WatercolourErrorCode;

  constructor(code: WatercolourErrorCode, message: string, options?: { cause?: unknown }) {
    super(message, options);
    this.name = 'WatercolourError';
    this.code = code;
  }
}

const KNOWN_CODES: ReadonlySet<string> = new Set<WatercolourErrorCode>([
  'WebGpuUnavailable',
  'EngineUnavailable',
  'InitFailed',
  'InstanceLimit',
  'DeviceLost',
  'NoSurface',
  'InvalidScene',
  'InvalidDetail',
  'InvalidSurface',
  'UnknownArtwork',
  'UnknownPalette',
  'InvalidPalette',
  'InvalidStrip',
  'InvalidManifest',
  'AssetMissing',
  'UnsupportedVersion',
  'Disposed',
  'Engine',
  'Unknown',
]);

/** Normalises anything thrown by the wasm bindings (or by us) into a typed error. */
export function toWatercolourError(error: unknown): WatercolourError {
  if (error instanceof WatercolourError) return error;
  const message = error instanceof Error ? error.message : String(error);
  const match = /^([A-Za-z]+):\s*([\s\S]*)$/.exec(message);
  if (match && KNOWN_CODES.has(match[1]!)) {
    return new WatercolourError(match[1] as WatercolourErrorCode, match[2] ?? '', { cause: error });
  }
  return new WatercolourError('Unknown', message, { cause: error });
}
