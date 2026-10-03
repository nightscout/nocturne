<script module lang="ts">
  import type { GlucoseTileVariant } from "@nocturne/ui/glucose";

  /** What the tile showed before this wash: a range fill, or the loading skeleton. */
  type PriorFill = GlucoseTileVariant | "skeleton";

  // Module state, not instance state: the tile unmounts this wash whenever it paints none, and
  // the reading can move between the header tile and the Current Glucose widget, so it must
  // outlive any one mount. That also means one live wash at a time.
  let settledSeed = $state<number | null>(null);
  let fadedSeed = $state<number | null>(null);
  let shownFill: PriorFill = "skeleton";

  export interface TileFill {
    loading: boolean;
    stale: boolean;
    disconnected: boolean;
    variant: GlucoseTileVariant;
  }

  /**
   * Records the flat fill a host's tile shows while it paints no wash (loading, stale,
   * disconnected or neutral; see GlucoseValueIndicator's `background`), so the next wash blooms
   * over what was on screen. Call during component initialisation.
   */
  export function trackUnwashedFill(tile: () => TileFill): void {
    $effect(() => {
      const { loading, stale, disconnected, variant } = tile();
      if (loading) shownFill = "skeleton";
      else if (stale) shownFill = "neutral";
      else if (disconnected || variant === "neutral") shownFill = variant;
    });
  }
</script>

<script lang="ts">
  import { washInterior } from '$lib/watercolour-wash';
  import { Artwork, prefersReducedMotion } from "@nocturne/watercolour";
  import GlucoseTileBloom from "./GlucoseTileBloom.svelte";
  import type { PlayerState } from "@nocturne/watercolour";

  interface Props {
    mills: number | undefined;
    variant: GlucoseTileVariant;
    delta: number;
  }

  let { mills, variant, delta }: Props = $props();
  const washSeed = $derived((mills ?? 0) % 2_147_483_647);
  // Each reading paints its own stroke, over the fill that was on screen when it arrived. Keyed
  // by the derived seed, so it is taken again only when the seed changes.
  const reading = $derived({ seed: washSeed, priorFill: shownFill });
  const washPerReading = !prefersReducedMotion();
  const recolours = $derived(reading.priorFill !== variant);
  // With a 50% tail, this progress is past covered tick 140 for every stagger seed.
  const BLOOM_COVERED = 0.67;
  let spreadSeed = $state<number | null>(null);
  const spread = $derived(spreadSeed === reading.seed);

  let noBloomSeed = $state<number | null>(null);
  const noBloom = $derived(noBloomSeed === reading.seed);

  function markSpread() {
    if (spreadSeed === reading.seed) return;
    spreadSeed = reading.seed;
    shownFill = variant;
  }

  function onWashState(state: PlayerState) {
    if (state.finished) settledSeed = reading.seed;
    if (state.mode === "none" || state.error !== undefined) markSpread();
  }

  function onBloomState(state: PlayerState) {
    if (state.finished) settledSeed = reading.seed;
    if (state.mode === "none" || state.error !== undefined) noBloomSeed = reading.seed;
    if (noBloom || state.finished || state.progress >= BLOOM_COVERED) markSpread();
  }
  function onBloomProgress(progress: number) {
    if (progress >= BLOOM_COVERED) markSpread();
  }
  const faded = $derived(
    recolours
      ? spread && (settledSeed === reading.seed || noBloom)
      : settledSeed === reading.seed
  );

  function markFaded(event: TransitionEvent) {
    if (event.target === event.currentTarget && faded) fadedSeed = reading.seed;
  }
  // A settled reading unmounted mid-fade must not replay on its next mount.
  function fadedOnUnmount(_node: HTMLElement) {
    return {
      destroy: () => {
        if (faded) fadedSeed = reading.seed;
      },
    };
  }
  const priorFillClass: Record<PriorFill, string> = {
    skeleton: "bg-accent",
    neutral: "bg-muted",
    "very-low": "bg-glucose-very-low",
    low: "bg-glucose-low",
    "in-range": "bg-glucose-in-range",
    high: "bg-glucose-high",
    "very-high": "bg-glucose-very-high",
  };

  // Soft-light keeps the range token's tone and the digits' contrast. The very-low and very-high
  // tiles carry light digits in most themes, so their wash only darkens, faintly, and never lifts
  // the fill toward the digits.
  const washBlend = $derived(
    variant === "very-low" || variant === "very-high" ? "mix-blend-multiply opacity-60" : "mix-blend-soft-light"
  );
</script>

{#if washPerReading}
  {#key reading.seed}
    {#if fadedSeed !== reading.seed}
      {#if recolours}
        <span
          class="absolute inset-0 prior-fill {priorFillClass[reading.priorFill]}"
          class:gone={spread}
          data-testid="glucose-tile-prior-fill"
        ></span>
        <span
          class="absolute inset-0 bloom"
          class:faded
          ontransitionend={markFaded}
          ontransitioncancel={markFaded}
          use:fadedOnUnmount
        >
          <GlucoseTileBloom seed={reading.seed} {delta} token="--glucose-{variant}" onstatechange={onBloomState} onprogress={onBloomProgress} />
        </span>
      {:else}
        <span
          class="absolute inset-0 wash-grain wash-fade {washBlend}"
          class:faded
          ontransitionend={markFaded}
          ontransitioncancel={markFaded}
          use:fadedOnUnmount
        >
          <Artwork
            artwork="wash"
            palette="slate"
            surface="light"
            seed={reading.seed}
            durationMs={11600}
            tail={0.86}
            releaseAfterFinish
            onstatechange={onWashState}
            fit="fill"
            crop={washInterior}
            class="size-full"
          />
        </span>
      {/if}
    {/if}
  {/key}
{:else}
  {#key variant}
    <span class="absolute inset-0 wash-grain {washBlend}">
      <Artwork artwork="wash" palette="slate" surface="light" releaseAfterFinish fit="fill" crop={washInterior} class="size-full" />
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
  /* Must outrank the blend opacity utility. */
  .wash-fade.faded {
    opacity: 0;
  }
  .prior-fill,
  .bloom {
    transition: opacity 1.6s ease-in-out;
  }
  .prior-fill.gone,
  .bloom.faded {
    opacity: 0;
  }
</style>
