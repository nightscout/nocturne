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
    colorFocusGradient,
    resolveColorFocusRange,
    resolveGlucoseColorThresholds,
    DEFAULT_GLUCOSE_COLOR_THRESHOLDS,
    GLUCOSE_COLOR_MIN,
    GLUCOSE_COLOR_MAX,
    insertSliderSteps,
    type ColorFocusRange,
    type GlucoseColorThresholds,
  } from "$lib/utils/metric-color-focus";

  let {
    metricLabel = "Average glucose",
    unit = "",
    observedMax = 1,
    cssVar = "--primary",
    fixedMax,
    focusRange = null,
    onFocusRangeChange = () => {},
    glucose = false,
    units = "mg/dl",
    thresholds = DEFAULT_GLUCOSE_COLOR_THRESHOLDS,
    stops = [],
    onThresholdsChange = () => {},
  }: {
    metricLabel?: string;
    unit?: string;
    observedMax?: number;
    cssVar?: string;
    fixedMax?: number;
    focusRange?: ColorFocusRange | null;
    onFocusRangeChange?: (range: [number, number] | null) => void;
    glucose?: boolean;
    units?: GlucoseUnits;
    thresholds?: GlucoseColorThresholds;
    stops?: ReadonlyArray<{ mgdl: number; color: string }>;
    onThresholdsChange?: (values: GlucoseColorThresholds | null) => void;
  } = $props();

  const id = $props.id();
  const automaticMax = $derived(
    fixedMax ??
      (Number.isFinite(observedMax) && observedMax > 0 ? observedMax : 1)
  );
  const values: readonly number[] = $derived(
    glucose ? thresholds : (focusRange ?? [0, automaticMax])
  );
  const minimum = $derived(glucose ? GLUCOSE_COLOR_MIN : 0);
  const maximum = $derived(
    glucose
      ? GLUCOSE_COLOR_MAX
      : (fixedMax ?? Math.max(automaticMax, values[1], 1))
  );
  const unitLabel = $derived(glucose ? getUnitLabel(units) : unit);
  const labels = $derived(
    glucose ? ["Very low", "Low", "High", "Very high"] : ["minimum", "maximum"]
  );
  const inputStep = $derived(glucose ? (units === "mmol" ? 0.1 : 1) : "any");
  const gradient = $derived(
    glucose
      ? `linear-gradient(to right in srgb, ${stops.map((stop) => `${stop.color} ${((stop.mgdl - minimum) / (maximum - minimum)) * 100}%`).join(", ")})`
      : colorFocusGradient(resolveColorFocusRange(values)!, maximum, cssVar)
  );
  const baseSliderSteps = $derived.by(() => {
    if (glucose) {
      const step = units === "mmol" ? 0.1 : 1;
      const first = Math.ceil(convertToDisplayUnits(minimum, units) / step);
      const last = Math.floor(convertToDisplayUnits(maximum, units) / step);
      return Array.from({ length: last - first + 1 }, (_, i) =>
        convertFromDisplayUnits((first + i) * step, units)
      ).filter((value) => value > minimum && value < maximum);
    }
    const step = Math.max(0.1, maximum / 10_000);
    const count = Math.min(10_000, Math.floor(maximum / step));
    return Array.from({ length: count + 1 }, (_, i) =>
      Number((i * step).toPrecision(12))
    );
  });
  // Bits UI snaps even untouched values; insert exact selections into the cached steps.
  const sliderSteps = $derived(
    insertSliderSteps(baseSliderSteps, glucose ? values : [...values, maximum])
  );
  const resetLabel = $derived(
    glucose
      ? "Reset average glucose color boundaries"
      : `Reset ${metricLabel} color range to automatic`
  );
  let drafts = $state<(number | undefined)[]>([]);
  let invalidBound = $state<number | null>(null);
  const display = (value: number) =>
    glucose ? convertToDisplayUnits(value, units) : value;
  const formatted = (value: number) =>
    glucose ? formatGlucoseValue(value, units) : String(value);
  const accessibleLabel = (index: number) =>
    `${metricLabel} ${labels[index]} color ${glucose ? "boundary" : "value"}`;

  $effect(() => {
    drafts = values.map(display);
    invalidBound = null;
  });

  function change(candidate: number[]): boolean {
    if (glucose) {
      const next = resolveGlucoseColorThresholds(candidate);
      if (!next) return false;
      onThresholdsChange(next);
    } else {
      const next = resolveColorFocusRange(candidate);
      if (!next || (fixedMax !== undefined && next[1] > fixedMax)) return false;
      onFocusRangeChange([next[0], next[1]]);
    }
    return true;
  }

  function changeBound(
    index: number,
    event: Event & { currentTarget: HTMLInputElement }
  ) {
    const input = event.currentTarget;
    if (!input.value || !Number.isFinite(input.valueAsNumber)) {
      invalidBound = index;
      return;
    }
    if (input.valueAsNumber === display(values[index])) {
      invalidBound = null;
      return;
    }
    const next = [...values];
    next[index] = glucose
      ? convertFromDisplayUnits(input.valueAsNumber, units)
      : input.valueAsNumber;
    invalidBound = change(next) ? null : index;
  }

  function reset() {
    if (glucose) onThresholdsChange(null);
    else onFocusRangeChange(null);
    drafts = (
      glucose ? DEFAULT_GLUCOSE_COLOR_THRESHOLDS : [0, automaticMax]
    ).map(display);
    invalidBound = null;
  }
</script>

<div
  class="color-focus w-full max-w-[420px] min-w-0 text-xs text-muted-foreground"
  data-testid={glucose ? "glucose-color-focus" : "color-focus"}
>
  <div class="print:hidden">
    <Slider.Root
      type="multiple"
      min={minimum}
      max={maximum}
      step={sliderSteps}
      autoSort={false}
      thumbPositioning="exact"
      bind:value={
        () => [...values],
        (next) => {
          change(next);
        }
      }
      class="relative flex h-10 w-full touch-none select-none items-center"
      aria-label={glucose
        ? "Average glucose color boundaries"
        : `${metricLabel} color focus`}
      aria-describedby={id + "-description"}
    >
      {#snippet children({ thumbItems })}
        <span
          class="h-3.5 w-full rounded-sm"
          style:background={gradient}
          role="img"
          aria-label={`${metricLabel} color scale from ${formatted(minimum)} to ${formatted(maximum)} ${unitLabel}`}
          data-testid={glucose ? "glucose-color-track" : "color-focus-track"}
        ></span>
        {#each thumbItems as thumb (thumb.index)}
          <Slider.Thumb
            index={thumb.index}
            aria-label={accessibleLabel(thumb.index)}
            aria-valuetext={`${formatted(thumb.value)} ${unitLabel}`}
            class="block size-5 shrink-0 rounded-full border-2 border-foreground bg-background shadow-sm before:absolute before:-inset-3 focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-ring/50"
          />
        {/each}
      {/snippet}
    </Slider.Root>
    <div class="mb-2 flex justify-between tabular-nums" aria-hidden="true">
      <span>{formatted(minimum)} {unitLabel}</span>
      <span>{formatted(maximum)} {unitLabel}</span>
    </div>
    <div
      class={glucose
        ? "grid grid-cols-2 gap-2 sm:grid-cols-4"
        : "flex flex-wrap items-end gap-2"}
    >
      {#each labels as label, index}
        <div class="min-w-0">
          <label for={id + "-bound-" + index} class="mb-1 block">
            {glucose
              ? `Color: ${label.toLowerCase()}`
              : index === 0
                ? "Min"
                : "Max"}
          </label>
          <Input
            id={id + "-bound-" + index}
            type="number"
            inputmode="decimal"
            min={glucose ? display(minimum) : 0}
            max={glucose ? display(maximum) : fixedMax}
            step={inputStep}
            bind:value={drafts[index]}
            oninput={(event: Event & { currentTarget: HTMLInputElement }) =>
              changeBound(index, event)}
            aria-label={accessibleLabel(index)}
            aria-invalid={invalidBound === index}
            aria-describedby={invalidBound === index
              ? id + "-error"
              : id + "-description"}
            class={glucose
              ? "h-8 w-full px-2 text-xs tabular-nums"
              : "h-8 w-20 px-2 text-xs tabular-nums"}
          />
        </div>
      {/each}
      {#if !glucose}<span class="pb-2">{unitLabel}</span>{/if}
    </div>
    <div class="mt-2 flex items-center justify-between gap-2">
      <span>{glucose ? unitLabel : ""}</span>
      <Button
        variant="outline"
        size="sm"
        class="h-8 px-2 text-xs"
        aria-label={resetLabel}
        onclick={reset}
      >
        {glucose ? "Reset" : "Auto"}
      </Button>
    </div>
    {#if invalidBound !== null}
      <p id={id + "-error"} class="mt-2 text-destructive" role="alert">
        {#if glucose}Enter four increasing boundaries within the color scale.
          Boundaries cannot overlap or use the endpoints.
        {:else}Enter a minimum of 0 or more and a maximum greater than the
          minimum{fixedMax !== undefined
            ? `, up to ${fixedMax} ${unitLabel}`
            : ""}.{/if}
      </p>
    {/if}
  </div>
  <div class="hidden print:block">
    <div
      class="h-3.5 w-full rounded-sm"
      style:background={gradient}
      role="img"
      aria-label={metricLabel + " color scale"}
    ></div>
    <p class="mt-1">
      {metricLabel}: color {glucose ? "boundaries" : "focus"}
      {values.map(formatted).join(" / ")}
      {unitLabel}
    </p>
  </div>
  {#if glucose}
    <div
      class="mt-2 grid grid-cols-2 gap-x-3 gap-y-1 tabular-nums"
      aria-label="Glucose color zones"
    >
      <span>Very low color: &lt; {formatted(values[0])}</span>
      <span>Low color: {formatted(values[0])}–{formatted(values[1])}</span>
      <span>In range color: {formatted(values[1])}–{formatted(values[2])}</span>
      <span>High color: {formatted(values[2])}–{formatted(values[3])}</span>
      <span>Very high color: &gt; {formatted(values[3])} {unitLabel}</span>
    </div>
  {/if}
  <p id={id + "-description"} class="mt-2">
    {glucose
      ? "Color scale only; glucose targets and Time in Range are unchanged. Controls reshape the continuous gradient."
      : "Values outside the selected range use the end colors."}
  </p>
</div>

<style>
  .color-focus {
    print-color-adjust: exact;
    -webkit-print-color-adjust: exact;
  }
</style>
