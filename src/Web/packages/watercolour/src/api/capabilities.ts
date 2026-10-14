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
  requestAdapter(options?: { powerPreference?: 'high-performance' | 'low-power' }): Promise<unknown>;
}

/** What the engine asks WebGPU for, so the probe tests the adapter it will get. */
const ADAPTER_OPTIONS = { powerPreference: 'high-performance' } as const;

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

/** `onAdapter` receives the adapter the probe was handed, if any. */
export async function probeCapabilities(
  env: CapabilityEnvironment = browserEnvironment(),
  onAdapter?: (adapter: unknown) => void,
): Promise<Capabilities> {
  const reducedMotion = env.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
  const offscreenCanvas = env.offscreenCanvas ?? false;
  if (!env.gpu) {
    return { webgpu: false, adapter: false, reducedMotion, offscreenCanvas, reason: 'navigator.gpu is not available' };
  }
  try {
    const adapter = await env.gpu.requestAdapter(ADAPTER_OPTIONS);
    if (!adapter) {
      return { webgpu: true, adapter: false, reducedMotion, offscreenCanvas, reason: 'no WebGPU adapter' };
    }
    onAdapter?.(adapter);
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
let probedAdapter: unknown;

/** Probed once per page; `prefers-reduced-motion` is re-read live by callers that need it (see `playback`). */
export function detectCapabilities(): Promise<Capabilities> {
  cached ??= probeCapabilities(browserEnvironment(), (adapter) => (probedAdapter = adapter));
  return cached;
}

/**
 * The adapter the page probe was handed, once. It has opened no device, so
 * the engine can open its own on it instead of asking WebGPU a second time.
 */
export function takeProbedAdapter(): unknown {
  const adapter = probedAdapter;
  probedAdapter = undefined;
  return adapter;
}

export function resetCapabilitiesCache(): void {
  cached = undefined;
  probedAdapter = undefined;
}

/**
 * Whether a host should skip its own animation: the OS asks for reduced
 * motion, or the presentation preference is `still` or `off`.
 */
export function prefersReducedMotion(): boolean {
  if (getPresentation() !== 'animated') return true;
  return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
}
