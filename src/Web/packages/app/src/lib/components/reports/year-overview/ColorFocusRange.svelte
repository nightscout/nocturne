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
    resolveGlucoseFocusBand,
    DEFAULT_GLUCOSE_COLOR_THRESHOLDS,
    DEFAULT_GLUCOSE_FOCUS_BAND,
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
    focusBand = null,
    onFocusBandChange = () => {},
    lowColor = undefined,
    highColor = undefined,
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
    focusBand?: ColorFocusRange | null;
    onFocusBandChange?: (values: [number, number] | null) => void;
    lowColor?: string;
    highColor?: string;
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
  const focusBandValues: readonly number[] = $derived(
    focusBand ?? (glucose ? DEFAULT_GLUCOSE_FOCUS_BAND : [minimum, maximum])
  );
  const unitLabel = $derived(glucose ? getUnitLabel(units) : unit);
  const labels = $derived(
    glucose ? ["Very low", "Low", "High", "Very high"] : ["Min color", "Max color"]
  );
  const inputStep = $derived(glucose ? (units === "mmol" ? 0.1 : 1) : "any");
  const gradient = $derived(
    glucose
      ? `linear-gradient(to right in srgb, ${stops.map((stop) => `${stop.color} ${((stop.mgdl - minimum) / (maximum - minimum)) * 100}%`).join(", ")})`
      : colorFocusGradient(resolveColorFocusRange(values)!, maximum, cssVar, lowColor, highColor)
  );

  const activeBandLeftPercent = $derived.by(() => {
    return Math.max(0, Math.min(100, ((focusBandValues[0] - minimum) / (maximum - minimum)) * 100));
  });

  const activeBandRightPercent = $derived.by(() => {
    return Math.max(0, Math.min(100, ((focusBandValues[1] - minimum) / (maximum - minimum)) * 100));
  });

  const baseSliderSteps = $derived.by(() => {
    if (glucose) {
      const step = units === "mmol" ? 0.1 : 1;
      const first = Math.ceil(convertToDisplayUnits(minimum, units) / step);
      const last = Math.floor(convertToDisplayUnits(maximum, units) / step);
      return Array.from({ length: last - first + 1 }, (_, i) =>
        convertFromDisplayUnits((first + i) * step, units)
      ).filter((value) => value >= minimum && value <= maximum);
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
  const focusSliderSteps = $derived(
    insertSliderSteps(baseSliderSteps, [minimum, ...focusBandValues, maximum])
  );

  const resetLabel = $derived(
    glucose
      ? "Reset average glucose color boundaries"
      : `Reset ${metricLabel} color range to automatic`
  );

  let drafts = $state<(number | undefined)[]>([]);
  let focusDrafts = $state<(number | undefined)[]>([]);
  let invalidBound = $state<number | null>(null);
  let invalidFocusBound = $state<number | null>(null);

  const display = (value: number) =>
    glucose ? convertToDisplayUnits(value, units) : value;
  const formatted = (value: number) =>
    glucose ? formatGlucoseValue(value, units) : String(value);
  const accessibleLabel = (index: number) =>
    `${metricLabel} ${labels[index]} color ${glucose ? "boundary" : "value"}`;

  $effect(() => {
    drafts = values.map(display);
    invalidBound = null;
    focusDrafts = focusBandValues.map(display);
    invalidFocusBound = null;
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

  function changeFocusBand(candidate: number[]): boolean {
    const next = glucose
      ? resolveGlucoseFocusBand(candidate)
      : resolveColorFocusRange(candidate);
    if (!next || (fixedMax !== undefined && next[1] > fixedMax)) return false;
    onFocusBandChange([next[0], next[1]]);
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

  function changeFocusBound(
    index: number,
    event: Event & { currentTarget: HTMLInputElement }
  ) {
    const input = event.currentTarget;
    if (!input.value || !Number.isFinite(input.valueAsNumber)) {
      invalidFocusBound = index;
      return;
    }
    if (input.valueAsNumber === display(focusBandValues[index])) {
      invalidFocusBound = null;
      return;
    }
    const next = [...focusBandValues];
    next[index] = glucose
      ? convertFromDisplayUnits(input.valueAsNumber, units)
      : input.valueAsNumber;
    invalidFocusBound = changeFocusBand(next) ? null : index;
  }

  function reset() {
    if (glucose) {
      onThresholdsChange(null);
      onFocusBandChange(null);
      drafts = DEFAULT_GLUCOSE_COLOR_THRESHOLDS.map(display);
      focusDrafts = DEFAULT_GLUCOSE_FOCUS_BAND.map(display);
    } else {
      onFocusRangeChange(null);
      onFocusBandChange(null);
      drafts = [0, automaticMax].map(display);
      focusDrafts = [0, automaticMax].map(display);
    }
    invalidBound = null;
    invalidFocusBound = null;
  }

  function resetFocusBand() {
    onFocusBandChange(null);
    focusDrafts = (glucose ? DEFAULT_GLUCOSE_FOCUS_BAND : [minimum, maximum]).map(display);
    invalidFocusBound = null;
  }
</script>

<div
  class="color-focus w-full min-w-0 text-xs text-muted-foreground"
  data-testid={glucose ? "glucose-color-focus" : "color-focus"}
>
  <div class="print:hidden">
    <div class="grid grid-cols-1 @2xl:grid-cols-[minmax(260px,1fr)_minmax(300px,1.2fr)] @2xl:items-start gap-x-6 gap-y-3">
      <!-- Left side: Slider track, min/max numbers, glucose zones & hints -->
      <div class="space-y-1.5 min-w-0">
        <div class="relative flex h-10 w-full items-center">
          <!-- 1. Color gradient & Color scaling bullets -->
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
                class="relative h-3.5 w-full rounded-sm overflow-hidden"
                style:background={gradient}
                role="img"
                aria-label={`${metricLabel} color scale from ${formatted(minimum)} to ${formatted(maximum)} ${unitLabel}`}
                data-testid={glucose ? "glucose-color-track" : "color-focus-track"}
              >
                {#if activeBandLeftPercent > 0}
                  <span
                    class="absolute left-0 top-0 bottom-0 bg-background/85 pointer-events-none"
                    style:width="{activeBandLeftPercent}%"
                  ></span>
                {/if}
                {#if activeBandRightPercent < 100}
                  <span
                    class="absolute right-0 top-0 bottom-0 bg-background/85 pointer-events-none"
                    style:left="{activeBandRightPercent}%"
                  ></span>
                {/if}
              </span>
              {#each thumbItems as thumb (thumb.index)}
                <Slider.Thumb
                  index={thumb.index}
                  aria-label={accessibleLabel(thumb.index)}
                  aria-valuetext={`${formatted(thumb.value)} ${unitLabel}`}
                  class="block size-5 shrink-0 rounded-full border-2 border-foreground bg-background shadow-sm before:absolute before:-inset-3 focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-ring/50 cursor-pointer"
                />
              {/each}
            {/snippet}
          </Slider.Root>

          <!-- 2. Focus lines (90% transparency boundary) -->
          <Slider.Root
            type="multiple"
            min={minimum}
            max={maximum}
            step={focusSliderSteps}
            autoSort={false}
            thumbPositioning="exact"
            bind:value={
              () => [...focusBandValues],
              (next) => {
                changeFocusBand(next);
              }
            }
            class="absolute inset-0 flex h-10 w-full touch-none select-none items-center pointer-events-none"
            aria-label={`${metricLabel} focus lines`}
          >
            {#snippet children({ thumbItems })}
              {#each thumbItems as thumb (thumb.index)}
                <Slider.Thumb
                  index={thumb.index}
                  aria-label={thumb.index === 0 ? "Focus minimum line" : "Focus maximum line"}
                  aria-valuetext={`${formatted(thumb.value)} ${unitLabel}`}
                  class="pointer-events-auto block w-2.5 h-7 -mt-0.5 rounded-sm border-2 border-foreground bg-background shadow-md cursor-ew-resize before:absolute before:-inset-3 focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-ring/50"
                />
              {/each}
            {/snippet}
          </Slider.Root>
        </div>

        <div class="flex justify-between tabular-nums text-[11px]" aria-hidden="true">
          <span>{formatted(minimum)} {unitLabel}</span>
          <span>{formatted(maximum)} {unitLabel}</span>
        </div>

        {#if glucose}
          <div
            class="grid grid-cols-2 gap-x-3 gap-y-1 tabular-nums text-[11px] pt-1 border-t border-border/40"
            aria-label="Glucose color zones"
          >
            <span>Very low: &lt; {formatted(values[0])}</span>
            <span>Low: {formatted(values[0])}–{formatted(values[1])}</span>
            <span>In range: {formatted(values[1])}–{formatted(values[2])}</span>
            <span>High: {formatted(values[2])}–{formatted(values[3])}</span>
            <span class="col-span-2">Very high: &gt; {formatted(values[3])} {unitLabel}</span>
          </div>
        {/if}
        <p id={id + "-description"} class="text-[11px] text-muted-foreground/80 pt-0.5">
          {glucose
            ? "Color scale and focus lines; values outside lines become transparent."
            : "Color intensity scale and focus lines; values outside lines become transparent."}
        </p>
      </div>

      <!-- Right side: Input controls & Reset actions -->
      <div class="space-y-2.5 min-w-0 @2xl:pl-2">
        <!-- Row 1: Kleur schaal bolletjes inputs -->
        <div
          class={glucose
            ? "grid grid-cols-2 gap-2 sm:grid-cols-4"
            : "flex flex-wrap items-end gap-2"}
        >
          {#each labels as label, index}
            <div class="min-w-0">
              <label for={id + "-bound-" + index} class="mb-1 block text-muted-foreground text-[11px]">
                {glucose ? `Color: ${label.toLowerCase()}` : label}
              </label>
              <Input
                id={id + "-bound-" + index}
                type="number"
                inputmode="decimal"
                min={display(minimum)}
                max={glucose ? display(maximum) : fixedMax}
                step={inputStep}
                bind:value={drafts[index]}
                oninput={(event: Event & { currentTarget: HTMLInputElement }) =>
                  changeBound(index, event)}
                aria-label={accessibleLabel(index)}
                aria-invalid={invalidBound === index}
                class={glucose
                  ? "h-8 w-full px-2 text-xs tabular-nums"
                  : "h-8 w-20 px-2 text-xs tabular-nums"}
              />
            </div>
          {/each}
          {#if !glucose}<span class="pb-2 text-[11px]">{unitLabel}</span>{/if}
        </div>

        <!-- Row 2: Focus lines (transparent) inputs -->
        <div class="pt-2 border-t border-border/50 flex flex-wrap items-center gap-2 text-xs">
          <span class="font-medium text-foreground/80 text-[11px]">Focus lines (transparent):</span>
          <div class="flex items-center gap-1.5">
            <label for={id + "-focus-min"} class="text-muted-foreground text-[11px]">Min:</label>
            <Input
              id={id + "-focus-min"}
              type="number"
              inputmode="decimal"
              min={display(minimum)}
              max={glucose ? display(maximum) : fixedMax}
              step={inputStep}
              bind:value={focusDrafts[0]}
              oninput={(event: Event & { currentTarget: HTMLInputElement }) =>
                changeFocusBound(0, event)}
              aria-invalid={invalidFocusBound === 0}
              class="h-7 w-20 px-2 text-xs tabular-nums"
            />
          </div>
          <div class="flex items-center gap-1.5">
            <label for={id + "-focus-max"} class="text-muted-foreground text-[11px]">Max:</label>
            <Input
              id={id + "-focus-max"}
              type="number"
              inputmode="decimal"
              min={display(minimum)}
              max={glucose ? display(maximum) : fixedMax}
              step={inputStep}
              bind:value={focusDrafts[1]}
              oninput={(event: Event & { currentTarget: HTMLInputElement }) =>
                changeFocusBound(1, event)}
              aria-invalid={invalidFocusBound === 1}
              class="h-7 w-20 px-2 text-xs tabular-nums"
            />
          </div>
          <Button
            variant="ghost"
            size="sm"
            class="h-7 px-2 text-xs text-muted-foreground"
            onclick={resetFocusBand}
          >
            Reset lines
          </Button>
        </div>

        <div class="flex items-center justify-between gap-2 pt-0.5">
          <span class="text-[11px] text-muted-foreground">
            {glucose ? unitLabel : ""}
          </span>
          <Button
            variant="outline"
            size="sm"
            class="h-7 px-2.5 text-xs"
            aria-label={resetLabel}
            onclick={reset}
          >
            {glucose ? "Reset all" : "Auto reset"}
          </Button>
        </div>

        {#if invalidBound !== null || invalidFocusBound !== null}
          <p id={id + "-error"} class="text-destructive text-[11px]" role="alert">
            {#if glucose}
              {invalidBound !== null
                ? "Enter four strictly increasing glucose color boundaries."
                : "Enter valid focus line boundaries (min < max)."}
            {:else}
              {invalidBound !== null
                ? `Enter valid color scale boundaries (min < max).`
                : `Enter valid focus line boundaries (min < max).`}
            {/if}
          </p>
        {/if}
      </div>
    </div>
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
</div>

<style>
  .color-focus {
    print-color-adjust: exact;
    -webkit-print-color-adjust: exact;
  }
</style>
