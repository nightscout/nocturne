import type { ArtworkMode, ArtworkMotion } from '../types';

export type ResolvedMode = 'live' | 'baked' | 'static' | 'none';

export interface ModeInputs {
  requested: ArtworkMode;
  motion: ArtworkMotion;
  capabilities: { webgpu: boolean; adapter: boolean; reducedMotion: boolean };
  /** The engine host has `maxLiveInstances` alive already. */
  capReached: boolean;
  hasBaked: boolean;
  hasStatic: boolean;
  /** The live backend renders one frame and releases. */
  releaseAfterFinish?: boolean;
  /**
   * The caller set a seed or an intensity, which the baked still (one seed,
   * default intensity) cannot show. Only then is a releasing live frame worth
   * simulating under reduced motion instead of drawing the still.
   */
  customised?: boolean;
}

export function resolveMotion(motion: ArtworkMotion, systemReducedMotion: boolean): 'full' | 'reduced' {
  if (motion === 'reduced') return 'reduced';
  if (motion === 'full') return 'full';
  return systemReducedMotion ? 'reduced' : 'full';
}

/**
 * Under reduced motion the static asset outranks a live engine. The engine
 * would only ever be asked for its last frame, at the cost of a GPU device.
 */
export function resolveMode(inputs: ModeInputs): ResolvedMode {
  const liveAvailable = inputs.capabilities.webgpu && inputs.capabilities.adapter && !inputs.capReached;
  const reduced = resolveMotion(inputs.motion, inputs.capabilities.reducedMotion) === 'reduced';
  switch (inputs.requested) {
    case 'live':
      if (liveAvailable) return 'live';
      return inputs.hasBaked ? 'baked' : inputs.hasStatic ? 'static' : 'none';
    case 'baked':
      return inputs.hasBaked ? 'baked' : inputs.hasStatic ? 'static' : 'none';
    case 'static':
      return inputs.hasStatic ? 'static' : inputs.hasBaked ? 'baked' : 'none';
    case 'auto':
      if (reduced) {
        if (inputs.releaseAfterFinish && inputs.customised) {
          if (liveAvailable) return 'live';
          if (inputs.hasStatic) return 'static';
          return inputs.hasBaked ? 'baked' : 'none';
        }
        if (inputs.hasStatic) return 'static';
        if (liveAvailable) return 'live';
        return inputs.hasBaked ? 'baked' : 'none';
      }
      if (liveAvailable) return 'live';
      if (inputs.hasBaked) return 'baked';
      return inputs.hasStatic ? 'static' : 'none';
  }
}

/** The chain a failing backend hands over to, in order. */
export function fallbackOrder(from: ResolvedMode, inputs: Pick<ModeInputs, 'hasBaked' | 'hasStatic'>): ResolvedMode[] {
  const chain: ResolvedMode[] = [];
  if (from === 'live' && inputs.hasBaked) chain.push('baked');
  if ((from === 'live' || from === 'baked') && inputs.hasStatic) chain.push('static');
  chain.push('none');
  return chain;
}
