<script lang="ts">
  import type { ArtworkOptions, FitMode, Surface } from '../types';
  import { type PlayerReadyCallback, artworkOptionsFrom, hostSurface, mountPlayer, type MountOptions } from './helpers';

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
    onready,
    class: className = '',
  }: {
    surface?: Surface;
    /** `contain` (default) preserves the artwork's aspect; `fill` stretches to the container. */
    fit?: FitMode;
    onready?: PlayerReadyCallback;
    class?: string;
  } & ArtworkOptions = $props();

  let frame: HTMLDivElement | undefined = $state();
  let canvas: HTMLCanvasElement | undefined = $state();

  // The card is usually taller than 3:1, so fill squashes the washes; the
  // background only fills when the host is already near its natural aspect.
  // The `bottom-left` contain anchor keeps the corner washes in the card's
  // corners rather than centring them mid-card.
  const backgroundFit: MountOptions['fit'] = (containerWidth, containerHeight) => {
    const aspect = containerWidth / Math.max(1, containerHeight);
    return Math.abs(aspect - 3) / 3 <= 0.2 ? 'fill' : 'contain';
  };

  // Luminous compositing saturates alpha on a dark ground, so the wash reads
  // as an opaque slab; dimming the canvas is a presentation-level correction.
  let darkSurface = $state(false);
  $effect(() => {
    darkSurface = (surface ?? hostSurface()) === 'dark';
  });

  $effect(() => {
    if (!frame || !canvas) return;
    return mountPlayer(
      frame,
      canvas,
      {
        artwork: 'confirmation-background',
        ...artworkOptionsFrom({ palette, seed, intensity, durationMs, motion, quality, mode }, { autoplay: 'once' }),
        fit: fit ?? backgroundFit,
        surface,
        fitAnchor: 'bottom-left',
        releaseAfterFinish: true,
        onReady: onready,
      },
    );
  });
</script>

<div
  bind:this={frame}
  aria-hidden="true"
  class="pointer-events-none absolute inset-0 overflow-hidden rounded-[inherit] {className}"
>
  <canvas bind:this={canvas} class="block h-full w-full" style:opacity={darkSurface ? 0.45 : 1}></canvas>
</div>