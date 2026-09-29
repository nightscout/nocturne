<script lang="ts">
  import { type ArtworkId, type ArtworkOptions, type IconArtworkSource, type Surface, artworkAspect } from '../types';
  import { assetUrl } from '../api/assets';
  import { hostSurface, type PlayerReadyCallback } from './helpers';
  import Artwork from './Artwork.svelte';

  let {
    artwork,
    icon,
    side = 'right',
    size = 400,
    gap = 28,
    threshold = 0.04,
    wrap = true,
    palette,
    surface,
    assetBaseUrl,
    onready,
    class: className = '',
    ...options
  }: {
    artwork?: ArtworkId;
    icon?: IconArtworkSource;
    /** Which edge of the measure the paint hugs. */
    side?: 'left' | 'right';
    /** Painted width in CSS px once the hero engages; height follows the aspect. */
    size?: number;
    /** Space held between the painted silhouette and the text. */
    gap?: number;
    /**
     * Alpha at which the still's pixels start displacing text, 0..1.
     *
     * A wash fades out over many pixels, so a higher cutoff lets a line sit
     * on the faint outer edge of a stroke.
     */
    threshold?: number;
    /**
     * Whether text flows around the silhouette.
     *
     * A float displaces line boxes but not block backgrounds. Set this false
     * where the heading is followed by a card or a table, which would
     * otherwise slide under the paint. The host's container must then be
     * `position: relative`.
     */
    wrap?: boolean;
    surface?: Surface;
    assetBaseUrl?: string;
    onready?: PlayerReadyCallback;
    class?: string;
  } & ArtworkOptions = $props();

  const aspect = $derived(icon || !artwork ? 1 : artworkAspect(artwork));
  const height = $derived(Math.round(size / aspect));

  /**
   * The silhouette comes from the baked still, the paint from the live
   * reveal. Text is therefore laid out once against the finished shape and
   * never reflows mid-animation. A custom `seed` moves the live brushwork off
   * the baked still it was rendered from; the wrap is then approximate.
   *
   * `shape-outside: url()` needs a CORS-clean image. Bundled assets are
   * same-origin; a cross-origin `assetBaseUrl` leaves the float rectangular
   * rather than failing, which is the same layout the fallback already gets.
   */
  let shape: string | undefined = $state();
  $effect(() => {
    const id = icon ? `lucide-${icon.name}` : artwork;
    if (!id) return;
    let live = true;
    assetUrl({ id, palette, surface: surface ?? hostSurface() }, 'final', { assetBaseUrl })
      .then((url) => {
        if (live) shape = url;
      })
      .catch(() => {});
    return () => {
      live = false;
    };
  });
</script>

<div
  class="nwc-hero nwc-hero--{side} {wrap ? 'nwc-hero--wrap' : 'nwc-hero--gutter'} {className}"
  style:--nwc-hero-size="{size}px"
  style:--nwc-hero-height="{height}px"
  style:--nwc-hero-ratio="{size} / {height}"
  style:--nwc-hero-gap="{gap}px"
  style:--nwc-hero-threshold={threshold}
  style:--nwc-hero-shape={shape ? `url("${shape}")` : 'none'}
>
  <Artwork {artwork} {icon} {palette} {surface} {assetBaseUrl} {onready} class="h-full w-full" {...options} />
</div>

<style>
  /* Below the float breakpoint the gutter does not exist, so the paint is a
     plain block above the text at its natural aspect. */
  .nwc-hero {
    width: min(100%, var(--nwc-hero-size));
    /* A <ratio> takes plain numbers; the px sizes would void the declaration and collapse the box to no height. */
    aspect-ratio: var(--nwc-hero-ratio);
    margin: 0 auto var(--nwc-hero-gap);
  }

  @media (min-width: 1024px) {
    .nwc-hero {
      width: var(--nwc-hero-size);
      height: var(--nwc-hero-height);
      aspect-ratio: auto;
    }

    /*
     * The float carries no margin at all. `shape-outside: url()` fits the
     * image to the MARGIN box, and that reference box cannot be reselected
     * for an image shape, so any margin rescales the silhouette off the paint
     * it was cut from. `shape-margin` opens the gap instead, and it holds the
     * text off the silhouette itself rather than off the bounding box.
     */
    .nwc-hero--wrap {
      margin: 0;
      shape-outside: var(--nwc-hero-shape);
      shape-image-threshold: var(--nwc-hero-threshold);
      shape-margin: var(--nwc-hero-gap);
    }

    .nwc-hero--wrap.nwc-hero--right {
      float: right;
    }

    .nwc-hero--wrap.nwc-hero--left {
      float: left;
    }

    /* Parked clear of the measure, so a card below it keeps its own box. */
    .nwc-hero--gutter {
      position: absolute;
      top: 0;
      margin: 0;
    }

    .nwc-hero--gutter.nwc-hero--right {
      left: 100%;
      margin-left: var(--nwc-hero-gap);
    }

    .nwc-hero--gutter.nwc-hero--left {
      right: 100%;
      margin-right: var(--nwc-hero-gap);
    }
  }
</style>
