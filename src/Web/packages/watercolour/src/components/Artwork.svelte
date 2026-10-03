<script lang="ts">
  import { type ArtworkId, type ArtworkOptions, type FitMode, type IconArtworkSource, type Surface } from '../types';
  import { getPresentation, subscribePresentation } from '../api/presentation';
  import { iconSvg } from '../api/scenes';
  import type { CropWindow } from '../types';
  import type { PlayerProgressCallback } from '../api/playback';
  import { type PlayerReadyCallback, type PlayerStateCallback, hostSurface, mountPlayer } from './helpers';

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
    crop,
    surface,
    position = 'relative',
    assetBaseUrl,
    releaseAfterFinish,
    onready,
    onstatechange,
    onprogress,
    class: className = '',
  }: {
    artwork?: ArtworkId;
    /** A Lucide icon source; takes precedence over `artwork`. */
    icon?: IconArtworkSource;
    /** Defaults to the host theme: `.dark`/`.light` on `<html>`, then its computed `color-scheme`, then `prefers-color-scheme`. */
    surface?: Surface;
    /** `contain` (default) preserves the artwork's aspect; `fill` stretches to the container. */
    fit?: FitMode;
    crop?: CropWindow;
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
    /** See `PlayerProgressCallback`. */
    onprogress?: PlayerProgressCallback;
    class?: string;
  } & ArtworkOptions = $props();

  let frame: HTMLDivElement | undefined = $state();
  let canvas: HTMLCanvasElement | undefined = $state();

  let presentation = $state(getPresentation());
  $effect(() => subscribePresentation((value) => (presentation = value)));
  /** Turned off, an icon keeps its meaning as the plain Lucide glyph; anything else draws nothing. */
  const plainIcon = $derived(presentation === 'off' && icon ? iconSvg(icon.icon, surface ?? hostSurface()) : undefined);

  $effect(() => {
    if (plainIcon) return;
    if (!frame || !canvas) return;
    return mountPlayer(
      frame,
      canvas,
      {
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
        crop,
        surface,
        assetBaseUrl,
        releaseAfterFinish: releaseAfterFinish ?? autoplay !== 'never',
        onReady: onready,
        onStateChange: onstatechange,
        onProgress: onprogress,
      },
    );
  });
</script>

<div
  bind:this={frame}
  class="nwc-frame {className}"
  class:nwc-absolute={position === 'absolute'}
  class:nwc-fixed={position === 'fixed'}
  class:nwc-sticky={position === 'sticky'}
  aria-hidden="true"
  role="presentation"
>
  {#if plainIcon}
    <div class="nwc-plain-icon">{@html plainIcon}</div>
  {:else}
    <canvas bind:this={canvas}></canvas>
  {/if}
</div>

<style>
  .nwc-frame {
    position: relative;
    overflow: hidden;
  }
  .nwc-absolute { position: absolute; }
  .nwc-fixed { position: fixed; }
  .nwc-sticky { position: sticky; }
  canvas, .nwc-plain-icon {
    position: absolute;
    inset: 0;
    display: block;
  }
  .nwc-plain-icon :global(svg) {
    display: block;
    width: 100%;
    height: 100%;
  }
</style>
