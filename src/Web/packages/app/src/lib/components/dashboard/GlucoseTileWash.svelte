<script module lang="ts">
  import type { GlucoseTileVariant } from "@nocturne/ui/glucose";
  type PriorFill = GlucoseTileVariant | "skeleton";
  // Reading state survives handoffs between the header tile and the dashboard widget.
  let settledSeed = $state<number | null>(null);
  let fadedSeed = $state<number | null>(null);
  let shownFill: PriorFill = "skeleton";
</script>

<script lang="ts">
  import { untrack } from "svelte";
  import { Artwork, prefersReducedMotion } from "@nocturne/watercolour";
  import GlucoseTileBloom from "./GlucoseTileBloom.svelte";
  import type { PlayerState } from "@nocturne/watercolour";

  interface Props {
    mills: number | undefined;
    variant: GlucoseTileVariant;
    delta?: number;
  }

  let { mills, variant, delta = 0 }: Props = $props();
  const washSeed = $derived((mills ?? 0) % 2_147_483_647);
  const washPerReading = !prefersReducedMotion();
  const priorFill = $derived.by(() => {
    void washSeed;
    return untrack(() => shownFill);
  });
  const recolours = $derived(priorFill !== variant);
  // With a 50% tail, this progress is past covered tick 140 for every stagger seed.
  const BLOOM_COVERED = 0.67;
  let spreadSeed = $state<number | null>(null);
  const spread = $derived(spreadSeed === washSeed);

  let noBloomSeed = $state<number | null>(null);
  const noBloom = $derived(noBloomSeed === washSeed);

  function markSpread() {
    if (spreadSeed === washSeed) return;
    spreadSeed = washSeed;
    shownFill = variant;
  }

  function onWashState(state: PlayerState) {
    if (state.finished) settledSeed = washSeed;
    if (state.mode === "none" || state.error !== undefined) markSpread();
  }

  function onBloomState(state: PlayerState) {
    if (state.finished) settledSeed = washSeed;
    if (state.mode === "none" || state.error !== undefined) noBloomSeed = washSeed;
    if (noBloom || state.finished || state.progress >= BLOOM_COVERED) markSpread();
  }
  const faded = $derived(
    recolours
      ? spread && (settledSeed === washSeed || noBloom)
      : settledSeed === washSeed
  );

  function markFaded(event: TransitionEvent) {
    if (event.target === event.currentTarget && faded) fadedSeed = washSeed;
  }
  // A settled reading unmounted mid-fade must not replay on its next mount.
  function fadedOnUnmount(_node: HTMLElement) {
    return {
      destroy: () => {
        if (faded) fadedSeed = washSeed;
      },
    };
  }
  $effect(() => () => {
    shownFill = "neutral";
  });

  const priorFillClass: Record<PriorFill, string> = {
    skeleton: "bg-accent",
    neutral: "bg-muted",
    "very-low": "bg-glucose-very-low",
    low: "bg-glucose-low",
    "in-range": "bg-glucose-in-range",
    high: "bg-glucose-high",
    "very-high": "bg-glucose-very-high",
  };
  const bloomToken: Record<GlucoseTileVariant, string> = {
    neutral: "--muted",
    "very-low": "--glucose-very-low",
    low: "--glucose-low",
    "in-range": "--glucose-in-range",
    high: "--glucose-high",
    "very-high": "--glucose-very-high",
  };

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

{#if washPerReading}
  {#key washSeed}
    {#if fadedSeed !== washSeed}
      {#if recolours}
        <span class="absolute inset-0 prior-fill {priorFillClass[priorFill]}" class:gone={spread}></span>
        <span
          class="absolute inset-0 wash-tint"
          class:faded
          ontransitionend={markFaded}
          ontransitioncancel={markFaded}
          use:fadedOnUnmount
        >
          <GlucoseTileBloom seed={washSeed} {delta} token={bloomToken[variant]} onstatechange={onBloomState} />
        </span>
      {:else}
        <span
          class="absolute wash-crop wash-grain wash-fade {washBlend[variant]}"
          class:faded
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
            onstatechange={onWashState}
            fit="fill"
            class="size-full"
          />
        </span>
      {/if}
    {/if}
  {/key}
{:else}
  {#key variant}
    <span class="absolute wash-crop wash-grain {washBlend[variant]}">
      <Artwork artwork="wash" palette="slate" surface="light" releaseAfterFinish fit="fill" class="size-full" />
    </span>
  {/key}
{/if}

<style>
  .wash-crop {
    top: -100%;
    left: -46%;
    height: 303%;
    width: 192%;
  }
  /* Grayscale precedes brightening to avoid clipping individual pigment channels. */
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
  .wash-tint {
    transition: opacity 1.6s ease-in-out;
  }
  .prior-fill.gone,
  .wash-tint.faded {
    opacity: 0;
  }
</style>
