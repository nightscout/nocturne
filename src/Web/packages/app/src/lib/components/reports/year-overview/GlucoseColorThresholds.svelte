<script lang="ts">
  import { Slider } from "bits-ui";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import {
    convertToDisplayUnits,
    convertFromDisplayUnits,
    formatGlucoseValue,
    getUnitLabel,
    type GlucoseUnits,
  } from "$lib/utils/formatting";
  import {
    DEFAULT_GLUCOSE_COLOR_THRESHOLDS,
    GLUCOSE_COLOR_MIN,
    GLUCOSE_COLOR_MAX,
    resolveGlucoseColorThresholds,
    type GlucoseColorThresholds,
  } from "$lib/utils/metric-color-focus";

  let {
    units,
    thresholds = DEFAULT_GLUCOSE_COLOR_THRESHOLDS,
    onThresholdsChange,
    stops,
  }: {
    units: GlucoseUnits;
    thresholds?: GlucoseColorThresholds;
    onThresholdsChange: (value: GlucoseColorThresholds | null) => void;
    stops: ReadonlyArray<{ mgdl: number; color: string }>;
  } = $props();

  const id = $props.id();
  const minimum = GLUCOSE_COLOR_MIN;
  const maximum = GLUCOSE_COLOR_MAX;
  const labels = ["Very low", "Low", "High", "Very high"] as const;
  const unitLabel = $derived(getUnitLabel(units));
  const inputStep = $derived(units === "mmol" ? 0.1 : 1);
  const gradient = $derived(
    `linear-gradient(to right in srgb, ${stops
      .map(
        (stop) =>
          `${stop.color} ${((stop.mgdl - minimum) / (maximum - minimum)) * 100}%`
      )
      .join(", ")})`
  );
  const sliderSteps = $derived.by(() => {
    const first = Math.ceil(convertToDisplayUnits(minimum, units) / inputStep);
    const last = Math.floor(convertToDisplayUnits(maximum, units) / inputStep);
    const values = Array.from({ length: last - first + 1 }, (_, index) =>
      convertFromDisplayUnits((first + index) * inputStep, units)
    ).filter((value) => value > minimum && value < maximum);
    // Bits UI snaps supplied values to its steps, including boundaries not being edited.
    return [...new Set([...values, ...thresholds])].sort((a, b) => a - b);
  });
  let drafts = $state<(number | undefined)[]>([]);
  let invalidBound = $state<number | null>(null);

  $effect(() => {
    drafts = thresholds.map((threshold) =>
      convertToDisplayUnits(threshold, units)
    );
    invalidBound = null;
  });

  function changeSlider(values: number[]) {
    const next = resolveGlucoseColorThresholds(values);
    if (next) onThresholdsChange(next);
  }

  function changeBound(index: number, input: HTMLInputElement) {
    const value = input.valueAsNumber;
    if (!input.value || !Number.isFinite(value)) {
      invalidBound = index;
      return;
    }
    if (value === convertToDisplayUnits(thresholds[index], units)) {
      invalidBound = null;
      return;
    }
    const candidate = [...thresholds];
    candidate[index] = convertFromDisplayUnits(value, units);
    const next = resolveGlucoseColorThresholds(candidate);
    if (!next) {
      invalidBound = index;
      return;
    }
    invalidBound = null;
    onThresholdsChange(next);
  }

  function resetThresholds() {
    drafts = DEFAULT_GLUCOSE_COLOR_THRESHOLDS.map((threshold) =>
      convertToDisplayUnits(threshold, units)
    );
    invalidBound = null;
    onThresholdsChange(null);
  }
</script>

<div
  class="glucose-color-focus w-full max-w-[420px] min-w-0 text-xs text-muted-foreground"
>
  <div class="print:hidden">
    <Slider.Root
      type="multiple"
      min={minimum}
      max={maximum}
      step={sliderSteps}
      autoSort={false}
      thumbPositioning="exact"
      bind:value={() => [...thresholds], changeSlider}
      class="relative flex h-10 w-full touch-none select-none items-center"
      aria-label="Average glucose color boundaries"
      aria-describedby="{id}-description"
    >
      {#snippet children({ thumbItems })}
        <span
          class="h-3.5 w-full rounded-sm"
          style:background={gradient}
          data-glucose-color-track
        ></span>
        {#each thumbItems as thumb (thumb.index)}
          <Slider.Thumb
            index={thumb.index}
            aria-label="Average glucose {labels[thumb.index]} color boundary"
            aria-valuetext="{formatGlucoseValue(
              thumb.value,
              units
            )} {unitLabel}"
            class="block size-5 shrink-0 rounded-full border-2 border-foreground bg-background shadow-sm before:absolute before:-inset-3 focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-ring/50"
          />
        {/each}
      {/snippet}
    </Slider.Root>
    <div class="mb-2 flex justify-between tabular-nums" aria-hidden="true">
      <span>{formatGlucoseValue(minimum, units)} {unitLabel}</span>
      <span>{formatGlucoseValue(maximum, units)} {unitLabel}</span>
    </div>
    <div class="grid grid-cols-2 gap-2 sm:grid-cols-4">
      {#each labels as label, index}
        <div class="min-w-0">
          <label for="{id}-boundary-{index}" class="mb-1 block">{label}</label>
          <Input
            id="{id}-boundary-{index}"
            type="number"
            inputmode="decimal"
            min={convertToDisplayUnits(sliderSteps[0], units)}
            max={convertToDisplayUnits(
              sliderSteps[sliderSteps.length - 1],
              units
            )}
            step={inputStep}
            bind:value={drafts[index]}
            oninput={(event) => changeBound(index, event.currentTarget)}
            aria-label="Average glucose {label} color boundary"
            aria-invalid={invalidBound === index}
            aria-describedby={invalidBound === index
              ? `${id}-unit ${id}-error`
              : `${id}-unit`}
            class="h-8 w-full px-2 text-xs tabular-nums"
          />
        </div>
      {/each}
    </div>
    <div class="mt-2 flex items-center justify-between gap-2">
      <span id="{id}-unit">{unitLabel}</span>
      <Button
        variant="outline"
        size="sm"
        class="h-8 px-2 text-xs"
        aria-label="Reset average glucose color boundaries"
        onclick={resetThresholds}
      >
        Reset
      </Button>
    </div>
    {#if invalidBound !== null}
      <p id="{id}-error" class="mt-2 text-destructive" role="alert">
        Enter four increasing boundaries within the color scale. Boundaries
        cannot overlap or use the endpoints.
      </p>
    {/if}
  </div>
  <div class="hidden print:block">
    <div class="h-3.5 w-full rounded-sm" style:background={gradient}></div>
    <div class="mt-1 flex justify-between tabular-nums">
      <span>{formatGlucoseValue(minimum, units)} {unitLabel}</span>
      <span>{formatGlucoseValue(maximum, units)} {unitLabel}</span>
    </div>
    <p class="mt-2">Average glucose color boundaries:</p>
    <div class="mt-1 grid grid-cols-2 gap-x-4 gap-y-1 tabular-nums">
      {#each labels as label, index}
        <span>
          {label}: {formatGlucoseValue(thresholds[index], units)}
          {unitLabel}
        </span>
      {/each}
    </div>
  </div>
  <p id="{id}-description" class="mt-2">
    Color scale only; glucose targets and Time in Range are unchanged.
  </p>
</div>

<style>
  .glucose-color-focus {
    print-color-adjust: exact;
    -webkit-print-color-adjust: exact;
  }
</style>
