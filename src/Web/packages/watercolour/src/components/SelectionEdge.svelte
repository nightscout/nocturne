<script lang="ts">
  import type { ArtworkOptions, FitMode, Surface } from '../types';
  import { type PlayerReadyCallback, artworkOptionsFrom, mountPlayer } from './helpers';

  let {
    active = false,
    side = 'left',
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
    side?: 'left' | 'top';
    surface?: Surface;
    /** `fill` (default) runs the stroke the item's full length; `contain` keeps the artwork's aspect. */
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
        artwork: 'selection-edge',
        ...artworkOptionsFrom({ palette, seed, intensity, durationMs, motion, quality, mode }, { autoplay: 'once' }),
        fit: fit ?? 'fill',
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
  class="pointer-events-none absolute transition-opacity duration-300 {side === 'left'
    ? 'inset-y-0 left-0 w-4'
    : 'inset-x-0 top-0 h-4'} {active ? 'opacity-100' : 'opacity-0'} {className}"
>
  {#if active}
    <canvas bind:this={canvas} class="block h-full w-full"></canvas>
  {/if}
</span>