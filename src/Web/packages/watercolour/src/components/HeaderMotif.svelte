<script lang="ts">
  import type { ArtworkOptions, FitMode, Surface } from '../types';
  import { type PlayerReadyCallback, artworkOptionsFrom, mountPlayer } from './helpers';

  let {
    palette,
    seed,
    intensity,
    durationMs,
    motion,
    quality,
    mode,
    fit,
    surface,
    position = 'relative',
    onready,
    class: className = '',
  }: {
    surface?: Surface;
    /** `contain` (default) preserves the artwork's aspect; `fill` stretches to the container. */
    fit?: FitMode;
    /** The frame's `position`; see Artwork. */
    position?: 'relative' | 'absolute' | 'fixed' | 'sticky';
    onready?: PlayerReadyCallback;
    class?: string;
  } & ArtworkOptions = $props();

  let frame: HTMLDivElement | undefined = $state();
  let canvas: HTMLCanvasElement | undefined = $state();

  $effect(() => {
    if (!frame || !canvas) return;
    return mountPlayer(
      frame,
      canvas,
      {
        artwork: 'header-motif',
        ...artworkOptionsFrom({ palette, seed, intensity, durationMs, motion, quality, mode }, { autoplay: 'once' }),
        fit,
        surface,
        releaseAfterFinish: true,
        onReady: onready,
      },
    );
  });
</script>

<div
  bind:this={frame}
  aria-hidden="true"
  style="aspect-ratio: 5 / 1; width: 10rem"
  style:position
  class="overflow-hidden {className}"
>
  <canvas bind:this={canvas} class="block h-full w-full"></canvas>
</div>