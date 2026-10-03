<script lang="ts">
  import type { ArtworkOptions, FitMode, Surface } from '../types';
  import { type PlayerReadyCallback, artworkOptionsFrom, mountPlayer } from './helpers';

  let {
    active = false,
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
    active?: boolean;
    surface?: Surface;
    /** `contain` (default) preserves the artwork's aspect; `fill` stretches to the container. */
    fit?: FitMode;
    onready?: PlayerReadyCallback;
    class?: string;
  } & ArtworkOptions = $props();

  let frame: HTMLSpanElement | undefined = $state();
  let canvas: HTMLCanvasElement | undefined = $state();

  $effect(() => {
    if (!active || !frame || !canvas) return;
    return mountPlayer(
      frame,
      canvas,
      {
        artwork: 'tab-underline',
        ...artworkOptionsFrom({ palette, seed, intensity, durationMs, motion, quality, mode }, { autoplay: 'once' }),
        fit,
        surface,
        releaseAfterFinish: true,
        onReady: onready,
      },
    );
  });
</script>

<span
  bind:this={frame}
  aria-hidden="true"
  class="pointer-events-none absolute inset-x-0 bottom-0 h-1.5 overflow-visible transition-opacity duration-300 {active ? 'opacity-100' : 'opacity-0'} {className}"
>
  {#if active}
    <canvas bind:this={canvas} class="block h-full w-full"></canvas>
  {/if}
</span>