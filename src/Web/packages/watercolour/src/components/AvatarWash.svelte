<script lang="ts">
  import { type ArtworkOptions, type FitMode, type Surface, seedFromName } from '../types';
  import { type PlayerReadyCallback, artworkOptionsFrom, mountPlayer } from './helpers';

  let {
    name,
    size = 32,
    palette,
    seed,
    intensity,
    durationMs,
    motion,
    quality,
    mode,
    fit,
    surface,
    onready,
    class: className = '',
  }: {
    name: string;
    size?: number;
    surface?: Surface;
    /** `contain` (default) preserves the artwork's aspect; `fill` stretches to the container. */
    fit?: FitMode;
    onready?: PlayerReadyCallback;
    class?: string;
  } & ArtworkOptions = $props();

  let frame: HTMLSpanElement | undefined = $state();
  let canvas: HTMLCanvasElement | undefined = $state();

  const resolvedSeed = $derived(seed ?? seedFromName(name));
  // A member list holds dozens of avatars. A live slot per head would exhaust
  // the cap and pin checkpoint memory, so each wash paints one frame live and
  // releases the engine (the canvas keeps the pixels); reduced motion because
  // the reveal is skipped anyway and the name's seed still differs per head.
  const resolvedMotion = $derived(motion ?? 'reduced');

  $effect(() => {
    if (!frame || !canvas) return;
    return mountPlayer(
      frame,
      canvas,
      'avatar-wash',
      {
        ...artworkOptionsFrom({ palette, seed: resolvedSeed, intensity, durationMs, motion: resolvedMotion, quality, mode }),
        fit,
        surface,
        releaseAfterFinish: true,
      },
      onready,
    );
  });
</script>

<span
  bind:this={frame}
  aria-hidden="true"
  style="width: {size}px; height: {size}px"
  class="relative inline-flex shrink-0 overflow-hidden rounded-full {className}"
>
  <canvas bind:this={canvas} class="absolute inset-0 block h-full w-full"></canvas>
</span>