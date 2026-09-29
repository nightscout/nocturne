<script lang="ts">
  import { type ArtworkId, type ArtworkOptions, type FitMode, type IconArtworkSource, type Surface } from '../types';
  import { getPresentation, subscribePresentation } from '../api/presentation';
  import { iconSvg } from '../api/scenes';
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
  {#if plainIcon}
    <div class="nwc-plain-icon" style="position:absolute;inset:0">{@html plainIcon}</div>
  {:else}
    <canvas bind:this={canvas} style="position:absolute;inset:0;display:block"></canvas>
  {/if}
</div>

<style>
  .nwc-plain-icon :global(svg) {
    display: block;
    width: 100%;
    height: 100%;
  }
</style>
