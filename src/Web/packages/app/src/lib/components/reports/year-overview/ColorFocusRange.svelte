<script lang="ts">
  import type { Snippet } from "svelte";
  import { Slider } from "bits-ui";
  import ArrowLeftRight from "@lucide/svelte/icons/arrow-left-right";
  import Plus from "@lucide/svelte/icons/plus";
  import X from "@lucide/svelte/icons/x";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { cssColorToHex } from "#lib/utils/css-color.js";
  import ColorSwatchInput from "./ColorSwatchInput.svelte";
  import {
    convertToDisplayUnits,
    convertFromDisplayUnits,
    formatGlucoseValue,
    getUnitLabel,
    type GlucoseUnits,
  } from "#lib/utils/formatting.js";
  import {
    addColorBoundary,
    insertSliderSteps,
    rampColor,
    resolveColorBoundaries,
    resolveColorFocusRange,
    scaleGradient,
    type ColorBoundary,
    type ColorEnds,
    type ColorFocusRange,
    type MetricScale,
  } from "#lib/utils/metric-color-focus.js";

  let {
    metricLabel,
    scale,
    limits,
    glucose = false,
    units = "mg/dl",
    unit = "",
    onWindowChange,
    onBoundariesChange,
    onEndsChange,
    onDimChange,
    onReset,
    leading,
    trailing,
  }: {
    metricLabel: string;
    scale: MetricScale;
    /** The furthest the focus window may reach. */
    limits: ColorFocusRange;
    /** Glucose values are canonical mg/dL and shown in `units`; its ramp keeps its own domain. */
    glucose?: boolean;
    units?: GlucoseUnits;
    unit?: string;
    onWindowChange: (window: ColorFocusRange | null) => void;
    onBoundariesChange: (boundaries: ColorBoundary[] | null) => void;
    onEndsChange: (ends: ColorEnds) => void;
    onDimChange: (dim: number) => void;
    onReset: () => void;
    leading?: Snippet;
    trailing?: Snippet;
  } = $props();

  const id = $props.id();
  const unitLabel = $derived(glucose ? getUnitLabel(units) : unit);
  const step = $derived(glucose ? (units === "mmol" ? 0.1 : 1) : undefined);
  const display = (value: number) => (glucose ? convertToDisplayUnits(value, units) : value);
  const fromDisplay = (value: number) => (glucose ? convertFromDisplayUnits(value, units) : value);
  const formatted = (value: number) =>
    glucose ? formatGlucoseValue(value, units) : String(Math.round(value * 1000) / 1000);
  const snap = (value: number) =>
    step ? fromDisplay(Math.round(display(value) / step) * step) : Number(value.toPrecision(3));

  const sliderValues = $derived([...scale.window, ...scale.boundaries.map((b) => b.value)]);
  const percent = (value: number) =>
    Math.max(0, Math.min(100, ((value - scale.axis[0]) / (scale.axis[1] - scale.axis[0] || 1)) * 100));
  const gradient = $derived(scaleGradient(scale));
  const dimLeft = $derived(scale.window[0] > scale.axis[0] ? percent(scale.window[0]) : 0);
  const dimRight = $derived(scale.window[1] < scale.axis[1] ? percent(scale.window[1]) : 100);

  // Primitives, so a new scale with the same axis does not rebuild the steps.
  const axisMin = $derived(scale.axis[0]);
  const axisMax = $derived(scale.axis[1]);
  const baseSteps = $derived.by(() => {
    const [min, max] = [axisMin, axisMax];
    if (step) {
      const first = Math.ceil(display(min) / step);
      const last = Math.floor(display(max) / step);
      return Array.from({ length: last - first + 1 }, (_, i) => fromDisplay((first + i) * step)).filter(
        (value) => value >= min && value <= max
      );
    }
    const increment = Math.max(0.1, max / 10_000);
    const count = Math.min(10_000, Math.floor(max / increment));
    return Array.from({ length: count + 1 }, (_, i) => Number((i * increment).toPrecision(12)));
  });
  // Bits UI snaps even untouched values; insert exact selections into the cached steps.
  const sliderSteps = $derived(insertSliderSteps(baseSteps, [...sliderValues, ...scale.axis]));

  let invalid = $state<"window" | "boundary" | "dim" | null>(null);

  $effect(() => {
    void scale;
    invalid = null;
  });

  function changeWindow(candidate: number[]): boolean {
    const next = resolveColorFocusRange(candidate, limits);
    if (!next) return false;
    onWindowChange(next);
    return true;
  }

  function changeBoundary(index: number, value: number): boolean {
    const next = resolveColorBoundaries(
      scale.boundaries.map((b, i) => (i === index ? { ...b, value } : b)),
      limits
    );
    if (!next) return false;
    onBoundariesChange(next);
    return true;
  }

  function moveThumbs(next: number[]) {
    const index = next.findIndex((value, i) => value !== sliderValues[i]);
    if (index < 0) return;
    if (index < 2) changeWindow([next[0], next[1]]);
    else changeBoundary(index - 2, next[index]);
  }

  function typed(event: Event & { currentTarget: HTMLInputElement }): number | null {
    const input = event.currentTarget;
    return input.value && Number.isFinite(input.valueAsNumber) ? input.valueAsNumber : null;
  }

  function typeWindow(index: 0 | 1, event: Event & { currentTarget: HTMLInputElement }) {
    const value = typed(event);
    if (value === display(scale.window[index])) return void (invalid = null);
    const candidate = [...scale.window];
    if (value !== null) candidate[index] = fromDisplay(value);
    invalid = value !== null && changeWindow(candidate) ? null : "window";
  }

  function typeBoundary(index: number, event: Event & { currentTarget: HTMLInputElement }) {
    const value = typed(event);
    if (value === display(scale.boundaries[index].value)) return void (invalid = null);
    invalid = value !== null && changeBoundary(index, fromDisplay(value)) ? null : "boundary";
  }

  function typeDim(event: Event & { currentTarget: HTMLInputElement }) {
    const value = typed(event);
    if (value === null || value < 0 || value > 100) return void (invalid = "dim");
    invalid = null;
    onDimChange(value);
  }

  /** Null when no boundary fits: at the cap, or the widest gap snaps onto an existing one. */
  const added = $derived(addColorBoundary(scale, snap));

  const lowColor = $derived(rampColor(scale.ramp, 0));
  const highColor = $derived(rampColor(scale.ramp, 1));

  function colorBoundary(index: number, color: string) {
    onBoundariesChange(scale.boundaries.map((b, i) => (i === index ? { ...b, color } : b)));
  }

  function swapEnds(event: MouseEvent) {
    const host = event.currentTarget;
    if (!(host instanceof Element)) return;
    onEndsChange({ low: cssColorToHex(highColor, host), high: cssColorToHex(lowColor, host) });
  }

  function removeBoundary(index: number) {
    const next = scale.boundaries.filter((_, i) => i !== index);
    onBoundariesChange(next.length ? next : null);
  }
</script>

<div
  class="@container w-full min-w-0 text-xs text-muted-foreground print:hidden"
  data-testid={glucose ? "glucose-color-focus" : "color-focus"}
>
  <div
    class="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-4 gap-y-2 @3xl:grid-cols-[auto_minmax(0,1fr)_auto]"
  >
    <div class="col-start-1 row-start-1 min-w-0 @max-md:col-span-2">
      {@render leading?.()}
    </div>

    <div class="col-start-2 row-start-1 flex items-center justify-end gap-1.5 @max-md:col-span-2 @max-md:col-start-1 @max-md:row-start-2 @max-md:justify-start @3xl:col-start-3">
      <label for={id + "-dim"} class="whitespace-nowrap">Dim outside</label>
      <Input
        id={id + "-dim"}
        type="number"
        inputmode="numeric"
        min={0}
        max={100}
        value={scale.dim}
        oninput={typeDim}
        aria-invalid={invalid === "dim"}
        size="xs"
        class="w-14 tabular-nums"
      />
      <span>%</span>
      <Button variant="outline" size="xs" aria-label={`Reset ${metricLabel} color scale`} onclick={onReset}>
        Reset
      </Button>
      {@render trailing?.()}
    </div>

    <div class="col-span-2 row-start-2 min-w-0 @max-md:row-start-3 @3xl:col-span-1 @3xl:col-start-2 @3xl:row-start-1">
      <div class="relative h-8">
        <!-- Each value sits beside its handle, on the outside, and gives way rather than overlap the other. -->
        {#each [0, 1] as const as index (index)}
          <Input
            type="number"
            inputmode="decimal"
            min={display(limits[0])}
            max={Number.isFinite(limits[1]) ? display(limits[1]) : undefined}
            step={step ?? "any"}
            value={display(scale.window[index])}
            oninput={(event: Event & { currentTarget: HTMLInputElement }) => typeWindow(index, event)}
            aria-label={`${metricLabel} focus ${index === 0 ? "minimum" : "maximum"}`}
            aria-invalid={invalid === "window"}
            size="xs"
            class={[
              "absolute top-0 w-20 tabular-nums text-center",
              index === 0
                ? "left-[clamp(0px,calc(var(--at)-5rem),calc(100%-10.25rem))]"
                : "left-[clamp(5.25rem,var(--at),calc(100%-5rem))]",
            ]}
            style="--at: {percent(scale.window[index])}%"
          />
        {/each}
      </div>

      <Slider.Root
        type="multiple"
        min={scale.axis[0]}
        max={scale.axis[1]}
        step={sliderSteps}
        autoSort={false}
        thumbPositioning="exact"
        bind:value={() => [...sliderValues], moveThumbs}
        class="relative flex h-9 w-full touch-none select-none items-center"
        aria-label={`${metricLabel} color scale`}
      >
        {#snippet children({ thumbItems })}
          <span
            class="relative h-3.5 w-full overflow-hidden rounded-sm bg-(image:--scale-gradient)"
            style:--scale-gradient={gradient}
            role="img"
            aria-label={`${metricLabel} color scale from ${formatted(scale.axis[0])} to ${formatted(scale.axis[1])} ${unitLabel}`}
            data-testid={glucose ? "glucose-color-track" : "color-focus-track"}
          >
            {#if dimLeft > 0}
              <span
                class="pointer-events-none absolute inset-y-0 left-0 w-(--edge) bg-background opacity-(--dim)"
                style:--edge="{dimLeft}%"
                style:--dim={scale.dim / 100}
              ></span>
            {/if}
            {#if dimRight < 100}
              <span
                class="pointer-events-none absolute inset-y-0 right-0 left-(--edge) bg-background opacity-(--dim)"
                style:--edge="{dimRight}%"
                style:--dim={scale.dim / 100}
              ></span>
            {/if}
          </span>
          {#each thumbItems as thumb (thumb.index)}
            {#if thumb.index < 2}
              <Slider.Thumb
                index={thumb.index}
                aria-label={`${metricLabel} focus ${thumb.index === 0 ? "minimum" : "maximum"}`}
                aria-valuetext={`${formatted(thumb.value)} ${unitLabel}`}
                class="relative z-10 block h-7 w-2.5 cursor-ew-resize rounded-sm border-2 border-foreground bg-background shadow-md before:absolute before:-inset-3 focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-ring/50"
              />
            {:else}
              {@const boundary = scale.boundaries[thumb.index - 2]}
              <Slider.Thumb
                index={thumb.index}
                aria-label={`${metricLabel} color boundary ${thumb.index - 1}`}
                aria-valuetext={`${formatted(thumb.value)} ${unitLabel}`}
                class="relative z-20 block size-4 cursor-pointer rounded-full border-2 border-foreground bg-(--pin) shadow-sm before:absolute before:-inset-3 focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-ring/50"
                style="--pin: {rampColor(scale.ramp, boundary.position)}"
              />
            {/if}
          {/each}
        {/snippet}
      </Slider.Root>

      <div class="flex items-center justify-between gap-2 tabular-nums">
        <span class="flex items-center gap-1.5">
          <ColorSwatchInput
            color={lowColor}
            label={`${metricLabel} low end color`}
            onChange={(low) => onEndsChange({ ...scale.ends, low })}
          />
          <span aria-hidden="true">{formatted(scale.axis[0])} {unitLabel}</span>
        </span>
        {#if !glucose}
          <Button
            variant="ghost-muted"
            size="xs"
            aria-label={`Swap ${metricLabel} end colors`}
            onclick={swapEnds}
          >
            <ArrowLeftRight />
            Swap colors
          </Button>
        {/if}
        <span class="flex items-center gap-1.5">
          <span aria-hidden="true">{formatted(scale.axis[1])} {unitLabel}</span>
          <ColorSwatchInput
            color={highColor}
            label={`${metricLabel} high end color`}
            onChange={(high) => onEndsChange({ ...scale.ends, high })}
          />
        </span>
      </div>
    </div>

    <div
      class="col-span-2 row-start-3 flex @max-md:row-start-4 flex-wrap items-center gap-1.5 @3xl:col-span-1 @3xl:col-start-2 @3xl:row-start-2"
    >
      {#each scale.boundaries as boundary, index (boundary.position)}
        <div class="flex items-center gap-1">
          <ColorSwatchInput
            color={rampColor(scale.ramp, boundary.position)}
            label={`${metricLabel} color boundary ${index + 1} color`}
            onChange={(color) => colorBoundary(index, color)}
          />
          <Input
            type="number"
            inputmode="decimal"
            step={step ?? "any"}
            value={display(boundary.value)}
            oninput={(event: Event & { currentTarget: HTMLInputElement }) => typeBoundary(index, event)}
            aria-label={`${metricLabel} color boundary ${index + 1}`}
            aria-invalid={invalid === "boundary"}
            size="xs"
            class="w-16 tabular-nums"
          />
          <Button
            variant="ghost-muted"
            size="icon-xs"
            aria-label={`Remove ${metricLabel} color boundary ${index + 1}`}
            onclick={() => removeBoundary(index)}
          >
            <X />
          </Button>
        </div>
      {/each}
      <Button
        variant="outline"
        size="xs"
        disabled={!added}
        onclick={() => added && onBoundariesChange(added)}
      >
        <Plus />
        Color boundary
      </Button>
    </div>
  </div>

  {#if invalid}
    <p class="pt-2 text-destructive" role="alert">
      {#if invalid === "dim"}
        Enter a dim percentage between 0 and 100.
      {:else if invalid === "window"}
        Enter a focus window with its minimum below its maximum{Number.isFinite(limits[1])
          ? `, between ${formatted(limits[0])} and ${formatted(limits[1])} ${unitLabel}`
          : ""}.
      {:else}
        Keep each color boundary between its neighbours{Number.isFinite(limits[1])
          ? ` and inside ${formatted(limits[0])}–${formatted(limits[1])} ${unitLabel}`
          : ` and above ${formatted(limits[0])} ${unitLabel}`}.
      {/if}
    </p>
  {/if}
</div>
