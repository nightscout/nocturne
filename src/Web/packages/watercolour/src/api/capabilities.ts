import { getPresentation } from './presentation';

export interface Capabilities {
  /** `navigator.gpu` exists. */
  webgpu: boolean;
  /** `requestAdapter()` returned one. Chrome exposes `navigator.gpu` on machines with no usable adapter. */
  adapter: boolean;
  reducedMotion: boolean;
  offscreenCanvas: boolean;
  /** Why live mode is unavailable, when it is. */
  reason?: string;
}

interface GpuLike {
  requestAdapter(): Promise<unknown>;
}

export interface CapabilityEnvironment {
  gpu?: GpuLike | null;
  matchMedia?: (query: string) => { matches: boolean };
  offscreenCanvas?: boolean;
}

function browserEnvironment(): CapabilityEnvironment {
  const nav = typeof navigator === 'undefined' ? undefined : (navigator as Navigator & { gpu?: GpuLike });
  return {
    gpu: nav?.gpu ?? null,
    matchMedia: typeof matchMedia === 'function' ? (q) => matchMedia(q) : undefined,
    offscreenCanvas: typeof OffscreenCanvas !== 'undefined',
  };
}

export async function probeCapabilities(env: CapabilityEnvironment = browserEnvironment()): Promise<Capabilities> {
  const reducedMotion = env.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
  const offscreenCanvas = env.offscreenCanvas ?? false;
  if (!env.gpu) {
    return { webgpu: false, adapter: false, reducedMotion, offscreenCanvas, reason: 'navigator.gpu is not available' };
  }
  try {
    const adapter = await env.gpu.requestAdapter();
    if (!adapter) {
      return { webgpu: true, adapter: false, reducedMotion, offscreenCanvas, reason: 'no WebGPU adapter' };
    }
    return { webgpu: true, adapter: true, reducedMotion, offscreenCanvas };
  } catch (error) {
    return {
      webgpu: true,
      adapter: false,
      reducedMotion,
      offscreenCanvas,
      reason: `requestAdapter failed: ${error instanceof Error ? error.message : String(error)}`,
    };
  }
}

let cached: Promise<Capabilities> | undefined;

/** Probed once per page; `prefers-reduced-motion` is re-read live by callers that need it (see `playback`). */
export function detectCapabilities(): Promise<Capabilities> {
  cached ??= probeCapabilities();
  return cached;
}

export function resetCapabilitiesCache(): void {
  cached = undefined;
}

/**
 * Whether a host should skip its own animation: the OS asks for reduced
 * motion, or the presentation preference is `still` or `off`.
 */
export function prefersReducedMotion(): boolean {
  if (getPresentation() !== 'animated') return true;
  return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
}
