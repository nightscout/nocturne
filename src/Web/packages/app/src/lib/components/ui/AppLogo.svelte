<script lang="ts">
  import {
    FALLBACK_LOGO,
    monochromeLogos,
    resolveLogoName,
    resolveLogoSrc,
  } from "./logo-src";

  interface Props {
    /**
     * Icon identifier string (e.g., "dexcom", "xdrip", "loop") or filename with
     * extension (e.g., "mylogo.png")
     */
    icon: string | undefined;
    /** CSS class applied to the <img> element */
    class?: string;
    /**
     * When true, swap the dark/light logo variants so that the light logo
     * shows in light mode and the dark logo shows in dark mode (opposite of
     * the default sidebar behaviour).
     */
    invertMode?: boolean;
  }

  const { icon, class: className = "h-full w-full", invertMode = false }: Props = $props();

  const hasDarkVariant = $derived((icon ?? "device") === "nocturne");

  const src = $derived(resolveLogoSrc(icon));

  const isMonochrome = $derived(monochromeLogos.has(resolveLogoName(icon)));

  // An id with no asset would otherwise render a broken image. Track which src
  // failed rather than a bare flag, so a later icon isn't stuck on the fallback
  // and a missing fallback can't loop.
  let failedSrc = $state<string | null>(null);
  const displaySrc = $derived(failedSrc === src ? FALLBACK_LOGO : src);

  const lightSrc = $derived(hasDarkVariant ? "/logos/nocturne-light.png" : null);
</script>

{#if hasDarkVariant && lightSrc}
  <!-- Dark variant (nocturne.png = light logo for dark backgrounds) -->
  <img
    src={invertMode ? lightSrc : src}
    alt=""
    class="object-cover rounded-[inherit] dark:block hidden {className}"
    draggable="false"
  />
  <!-- Light variant (nocturne-light.png = dark logo for light backgrounds) -->
  <img
    src={invertMode ? src : lightSrc}
    alt=""
    class="object-cover rounded-[inherit] dark:hidden block {className}"
    draggable="false"
  />
{:else}
  <img
    src={displaySrc}
    alt=""
    class="object-cover rounded-[inherit] {isMonochrome
      ? 'dark:invert'
      : ''} {className}"
    draggable="false"
    onerror={() => (failedSrc = src)}
  />
{/if}
