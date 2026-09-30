<script module lang="ts">
  // Module state, not instance state: the tile unmounts this wash whenever it goes neutral, and
  // the reading can move between the header tile and the Current glucose widget, so the settled
  // and faded seeds must outlive any one mount. That also means one live wash at a time.
  let settledSeed = $state<number | null>(null);
  // Once a stroke has faded its canvas unmounts, so a remount of the same reading never replays
  // an invisible reveal.
  let fadedSeed = $state<number | null>(null);
</script>

<script lang="ts">
  import { Artwork, prefersReducedMotion } from "@nocturne/watercolour";
  import type { GlucoseTileVariant } from "@nocturne/ui/glucose";

  interface Props {
    /** The reading the wash belongs to; each reading paints its own stroke. */
    mills: number | undefined;
    variant: GlucoseTileVariant;
  }

  let { mills, variant }: Props = $props();

  // Each reading paints its own stroke, which settles and fades back to the bare fill well
  // before the next reading arrives.
  const washSeed = $derived((mills ?? 0) % 2_147_483_647);
  // Reduced motion or the Still preference keep one settled wash per range instead: a per-reading
  // stroke that fades would be animation they opted out of.
  const washPerReading = !prefersReducedMotion();

  function markFaded(event: TransitionEvent) {
    if (event.target === event.currentTarget && settledSeed === washSeed) fadedSeed = washSeed;
  }
  // A settled stroke unmounted mid-fade (the tile going neutral, or the reading moving to another
  // tile) counts as faded, or its remount would replay the reveal at opacity 0.
  function fadedOnUnmount(_node: HTMLElement) {
    return {
      destroy: () => {
        if (settledSeed === washSeed) fadedSeed = washSeed;
      },
    };
  }

  const softLight = "mix-blend-soft-light";
  const darkenOnly = "mix-blend-multiply opacity-60";
  const washBlend: Record<GlucoseTileVariant, string> = {
    "very-low": darkenOnly,
    low: softLight,
    "in-range": softLight,
    high: softLight,
    "very-high": darkenOnly,
    neutral: softLight,
  };
</script>

<!-- A grey wash soft-lit over the range fill: the tile keeps the range token's own hue in every
     theme, and soft-light (not multiply) lightens as much as it darkens, so the tile keeps the
     token's tone and the digits their contrast. The very-low and very-high tiles carry light
     digits in most themes, so their wash only darkens (multiply, faint) and can never lift the
     fill toward the digits. Cropped to the wash's interior so its dried edge
     falls outside the tile. -->
{#if washPerReading}
  {#key washSeed}
    {#if fadedSeed !== washSeed}
      <span
        class="absolute -top-full -left-[46%] h-[303%] w-[192%] wash-grain wash-fade {washBlend[variant]}"
        class:faded={settledSeed === washSeed}
        ontransitionend={markFaded}
        ontransitioncancel={markFaded}
        use:fadedOnUnmount
      >
        <Artwork
          artwork="wash"
          palette="slate"
          surface="light"
          seed={washSeed}
          durationMs={11600}
          tail={0.86}
          releaseAfterFinish
          onstatechange={(state) => {
            if (state.finished) settledSeed = washSeed;
          }}
          fit="fill"
          class="size-full"
        />
      </span>
    {/if}
  {/key}
{:else}
  {#key variant}
    <span class="absolute -top-full -left-[46%] h-[303%] w-[192%] wash-grain {washBlend[variant]}">
      <Artwork artwork="wash" palette="slate" surface="light" releaseAfterFinish fit="fill" class="size-full" />
    </span>
  {/key}
{/if}

<style>
  /* Grey first: brightening a tinted pigment clips its channels unevenly and the grain breaks up. */
  .wash-grain {
    filter: grayscale(1) brightness(1.5) contrast(1.6);
  }
  .wash-fade {
    transition: opacity 10s ease-out;
  }
  /* Outranks the blend's own opacity utility, so every variant fades to the bare fill. */
  .wash-fade.faded {
    opacity: 0;
  }
</style>
