<script lang="ts">
  import { type ArtworkId, type ArtworkOptions, type FitMode, type IconArtworkSource, type Surface } from '../types';
  import { type PlayerReadyCallback, type PlayerStateCallback, mountPlayer } from './helpers';

  let {
    artwork,
    icon,
    palette,
    seed,
    intensity,
    durationMs,
    easing,
    tail,
    motion,
    quality,
    mode,
    autoplay,
    fit,
    surface,
    position = 'relative',
    assetBaseUrl,
    releaseAfterFinish,
    onready,
    onstatechange,
    class: className = '',
  }: {
    artwork?: ArtworkId;
    /** A Lucide icon source; takes precedence over `artwork`. */
    icon?: IconArtworkSource;
    /** Defaults to the host theme: `.dark`/`.light` on `<html>`, then its computed `color-scheme`, then `prefers-color-scheme`. */
    surface?: Surface;
    /** `contain` (default) preserves the artwork's aspect; `fill` stretches to the container. */
    fit?: FitMode;
    /**
     * The frame's `position`. The canvas is absolute within it, so the frame
     * has to be a containing block; any value but `static` is one. Set
     * `absolute` or `fixed` to place the artwork itself rather than wrapping
     * it in a positioned element.
     */
    position?: 'relative' | 'absolute' | 'fixed' | 'sticky';
    assetBaseUrl?: string;
    /**
     * Lets go of the live engine once the reveal has finished and its frame
     * is on screen, keeping the pixels: a mounted artwork then holds no live
     * slot and no GPU memory. Defaults on unless `autoplay` is `never`, whose
     * host drives the player (`seek`, `play`) and needs it alive.
     */
    releaseAfterFinish?: boolean;
    /** Fires once a backend is drawing; the returned cleanup runs with the player's disposal. */
    onready?: PlayerReadyCallback;
    /** Fires on every player state change, including settling on `none`, where `onready` never fires. */
    onstatechange?: PlayerStateCallback;
    class?: string;
  } & ArtworkOptions = $props();

  let frame: HTMLDivElement | undefined = $state();
  let canvas: HTMLCanvasElement | undefined = $state();

  $effect(() => {
    if (!frame || !canvas) return;
    return mountPlayer(
      frame,
      canvas,
      artwork,
      {
        icon,
        palette,
        seed,
        intensity,
        durationMs,
        easing,
        tail,
        motion,
        quality,
        mode,
        autoplay,
        fit,
        surface,
        assetBaseUrl,
        releaseAfterFinish: releaseAfterFinish ?? autoplay !== 'never',
      },
      onready,
      onstatechange,
    );
  });
</script>

<div
  bind:this={frame}
  class={className}
  aria-hidden="true"
  role="presentation"
  style:position
  style:overflow="hidden"
>
  <canvas bind:this={canvas} style="position:absolute;inset:0;display:block"></canvas>
</div>
