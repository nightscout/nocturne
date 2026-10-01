<script module lang="ts">
  import type { GlucoseTileVariant } from "@nocturne/ui/glucose";

  /** What the tile showed before this wash: a range fill, or the loading skeleton. */
  type PriorFill = GlucoseTileVariant | "skeleton";

  // Module state, not instance state: the tile unmounts this wash whenever it goes neutral, and
  // the reading can move between the header tile and the Current glucose widget, so the settled
  // and faded seeds must outlive any one mount. That also means one live wash at a time.
  let settledSeed = $state<number | null>(null);
  // Once a stroke has faded its canvas unmounts, so a remount of the same reading never replays
  // an invisible reveal.
  let fadedSeed = $state<number | null>(null);
  // The fill the tile last settled on. Nothing yet means the page has only shown the skeleton.
  let shownFill: PriorFill = "skeleton";
</script>

<script lang="ts">
  import { untrack } from "svelte";
  import { Artwork, prefersReducedMotion } from "@nocturne/watercolour";
  import type { PlayerState } from "@nocturne/watercolour";

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

  // A reading in another range than the tile last showed paints its range's colour over the old
  // fill, which holds until the stroke has swept across the tile and then fades to the new one.
  // The wash artwork lays its whole stroke on the first tick, so the sweep is a mask over it.
  const priorFill = $derived.by(() => {
    void washSeed;
    return untrack(() => shownFill);
  });
  const recolours = $derived(priorFill !== variant);
  let paintingSeed = $state<number | null>(null);
  let spreadSeed = $state<number | null>(null);
  const spread = $derived(spreadSeed === washSeed);

  function markSpread() {
    if (spreadSeed === washSeed) return;
    spreadSeed = washSeed;
    shownFill = variant;
  }

  function onWashState(state: PlayerState) {
    if (state.finished) settledSeed = washSeed;
    if (state.mode === "none" || state.error !== undefined) markSpread();
    else if (state.mode !== "pending") paintingSeed = washSeed;
  }

  function onSweepEnd(event: AnimationEvent) {
    if (event.target === event.currentTarget) markSpread();
  }

  const faded = $derived(recolours ? spread : settledSeed === washSeed);

  function markFaded(event: TransitionEvent) {
    if (event.target === event.currentTarget && faded) fadedSeed = washSeed;
  }
  // A settled stroke unmounted mid-fade (the tile going neutral, or the reading moving to another
  // tile) counts as faded, or its remount would replay the reveal at opacity 0.
  function fadedOnUnmount(_node: HTMLElement) {
    return {
      destroy: () => {
        if (faded) fadedSeed = washSeed;
      },
    };
  }

  // The tile shows its flat neutral fill while this wash is unmounted.
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
  const floodClass: Record<GlucoseTileVariant, string> = {
    neutral: "flood-neutral",
    "very-low": "flood-very-low",
    low: "flood-low",
    "in-range": "flood-in-range",
    high: "flood-high",
    "very-high": "flood-very-high",
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

<!-- A grey wash soft-lit over the range fill: the tile keeps the range token's own hue in every
     theme, and soft-light (not multiply) lightens as much as it darkens, so the tile keeps the
     token's tone and the digits their contrast. The very-low and very-high tiles carry light
     digits in most themes, so their wash only darkens (multiply, faint) and can never lift the
     fill toward the digits. Cropped to the wash's interior so its dried edge
     falls outside the tile.
     A recolouring wash instead floods the stroke's coverage with the new range token, laid over
     the previous fill. -->
{#if washPerReading}
  <svg aria-hidden="true" class="absolute size-0">
    <filter id="glucose-tile-wash-tint" color-interpolation-filters="sRGB">
      <feComponentTransfer in="SourceAlpha" result="coverage">
        <feFuncA type="linear" slope="1.5" />
      </feComponentTransfer>
      <feColorMatrix
        in="SourceGraphic"
        type="matrix"
        values="0.12 0.24 0.04 0 0.8  0.12 0.24 0.04 0 0.8  0.12 0.24 0.04 0 0.8  0 0 0 0 1"
        result="grain"
      />
      <feFlood class={floodClass[variant]} result="hue" />
      <feBlend in="hue" in2="grain" mode="multiply" />
      <feComposite in2="coverage" operator="in" />
    </filter>
  </svg>
  {#key washSeed}
    {#if fadedSeed !== washSeed}
      {#if recolours}
        <span class="absolute inset-0 prior-fill {priorFillClass[priorFill]}" class:gone={spread}></span>
      {/if}
      <span
        class="absolute {recolours
          ? 'wash-tint -inset-x-[24%] -inset-y-[80%]'
          : `-top-full -left-[46%] h-[303%] w-[192%] wash-grain wash-fade ${washBlend[variant]}`}"
        class:faded
        class:sweeping={recolours && paintingSeed === washSeed}
        onanimationend={onSweepEnd}
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
  .wash-tint {
    filter: url(#glucose-tile-wash-tint);
    mask-image: linear-gradient(
      100deg,
      #000 calc(var(--sweep) - 22%),
      transparent var(--sweep)
    );
  }
  .wash-tint.sweeping {
    animation: tint-sweep 2.6s cubic-bezier(0.35, 0.55, 0.45, 1) forwards;
  }
  @property --sweep {
    syntax: "<percentage>";
    inherits: false;
    initial-value: 8%;
  }
  @keyframes tint-sweep {
    to {
      --sweep: 125%;
    }
  }
  .prior-fill,
  .wash-tint {
    transition: opacity 1.6s ease-in-out;
  }
  .prior-fill.gone,
  .wash-tint.faded {
    opacity: 0;
  }
  .flood-neutral {
    flood-color: var(--color-muted);
  }
  .flood-very-low {
    flood-color: var(--color-glucose-very-low);
  }
  .flood-low {
    flood-color: var(--color-glucose-low);
  }
  .flood-in-range {
    flood-color: var(--color-glucose-in-range);
  }
  .flood-high {
    flood-color: var(--color-glucose-high);
  }
  .flood-very-high {
    flood-color: var(--color-glucose-very-high);
  }
</style>
