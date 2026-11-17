<script lang="ts">
  import type { GlucoseTileVariant } from "@nocturne/ui/glucose";
  import { unwashed, washSeeds, type PriorFill } from "./glucose-tile-wash-state.svelte";
  import { washInterior } from '#lib/watercolour-wash.js';
  import { prefersReducedMotion } from "svelte/motion";
  import { Artwork } from "water-ui";
  import { illustrations } from "#lib/stores/illustrations.svelte.js";
  import GlucoseTileBloom from "./GlucoseTileBloom.svelte";
  import type { PlayerState } from "water-ui";
  import { untrack } from "svelte";
  import { deferredWatercolour } from "#lib/watercolour-deferral.svelte.js";

  interface Props {
    mills: number | undefined;
    variant: GlucoseTileVariant;
    delta: number | null;
  }

  let { mills, variant, delta }: Props = $props();
  const washSeed = $derived((mills ?? 0) % 2_147_483_647);
  // Reduced motion and the Still preference keep one settled wash per range: a stroke per reading
  // that fades is animation they opted out of.
  const washPerReading = $derived(illustrations.current === "animated" && !prefersReducedMotion.current);
  // A bloom is a scene with no still, so it boots the engine as it mounts; inside the
  // first-interaction window a reading that changes range takes the grey wash instead.
  const engineWindow = deferredWatercolour();
  // Each reading paints its own stroke, over the fill that was on screen when it arrived or when
  // strokes came back on. Keyed by the derived seed, so it is not retaken on every store update,
  // nor when the window ends mid-reading.
  const reading = $derived.by(() => {
    const bloomable = washPerReading && untrack(() => engineWindow.due);
    return { seed: washSeed, bloomable, priorFill: bloomable ? unwashed.fill : variant };
  });
  // The settled wash shows the flat range fill.
  $effect(() => {
    if (!reading.bloomable) unwashed.fill = variant;
  });
  const recolours = $derived(reading.priorFill !== variant);
  // With a 50% tail, this progress is past covered tick 140 for every stagger seed.
  const BLOOM_COVERED = 0.67;
  let spreadSeed = $state<number | null>(null);
  const spread = $derived(spreadSeed === reading.seed);

  let noBloomSeed = $state<number | null>(null);
  const noBloom = $derived(noBloomSeed === reading.seed);

  // The wash's chunk and first frame arrive after the tile's flat fill is on screen; held hidden
  // until then, it soaks into the fill rather than snapping on.
  let drawnSeed = $state<number | null>(null);
  let drawnVariant = $state<GlucoseTileVariant | null>(null);

  function markSpread() {
    if (spreadSeed === reading.seed) return;
    spreadSeed = reading.seed;
    unwashed.fill = variant;
  }

  function onWashState(state: PlayerState) {
    if (state.mode !== "none") drawnSeed = reading.seed;
    if (state.finished) washSeeds.settled = reading.seed;
    if (state.mode === "none" || state.error !== undefined) markSpread();
  }

  function onBloomState(state: PlayerState) {
    if (state.finished) washSeeds.settled = reading.seed;
    if (state.mode === "none" || state.error !== undefined) noBloomSeed = reading.seed;
    if (noBloom || state.finished || state.progress >= BLOOM_COVERED) markSpread();
  }
  function onBloomProgress(progress: number) {
    if (progress >= BLOOM_COVERED) markSpread();
  }
  const faded = $derived(
    recolours
      ? spread && (washSeeds.settled === reading.seed || noBloom)
      : washSeeds.settled === reading.seed
  );

  function markFaded(event: TransitionEvent) {
    if (event.target === event.currentTarget && faded) washSeeds.faded = reading.seed;
  }
  // A settled reading unmounted mid-fade must not replay on its next mount.
  function fadedOnUnmount(_node: HTMLElement) {
    return {
      destroy: () => {
        if (faded) washSeeds.faded = reading.seed;
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
    {#if washSeeds.faded !== reading.seed}
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
          <span class="absolute inset-0 wash-arrival" class:drawn={drawnSeed === reading.seed}>
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
        </span>
      {/if}
    {/if}
  {/key}
{:else}
  {#key variant}
    <span class="absolute inset-0 wash-grain {washBlend}">
      <span class="absolute inset-0 wash-arrival" class:drawn={drawnVariant === variant}>
        <Artwork
          artwork="wash"
          palette="slate"
          surface="light"
          releaseAfterFinish
          onstatechange={(state) => {
            if (state.mode !== "none") drawnVariant = variant;
          }}
          fit="fill"
          crop={washInterior}
          class="size-full"
        />
      </span>
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
  /* Inside the blended layer: an opacity below 1 outside it would isolate the blend from the fill
     while it fades. */
  .wash-arrival {
    opacity: 0;
    transition: opacity 0.9s ease-out;
  }
  .wash-arrival.drawn {
    opacity: 1;
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
