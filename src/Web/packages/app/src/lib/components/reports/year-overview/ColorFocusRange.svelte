<script lang="ts">
  import { Slider } from "bits-ui";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { getGlucoseHeatmapFill } from "$lib/utils/chart-colors";
  import {
    convertToDisplayUnits,
    convertFromDisplayUnits,
    formatGlucoseValue,
    getUnitLabel,
    type GlucoseUnits,
  } from "$lib/utils/formatting";
  import {
    colorFocusGradient,
    paletteSwatchGradient,
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
    metricKey = "avgGlucose",
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
    themeStops = undefined,
    onThresholdsChange = () => {},
    focusBand = null,
    onFocusBandChange = () => {},
    lowColor = undefined,
    highColor = undefined,
    colors = undefined,
    COLOR_PALETTES = [],
    onCustomColorsChange = () => {},
    invert = false,
    onInvertChange = () => {},
    transparencyPercent = 90,
    onTransparencyChange = () => {},
  }: {
    metricKey?: string;
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
    themeStops?: ReadonlyArray<{ mgdl: number; color: string }>;
    onThresholdsChange?: (values: GlucoseColorThresholds | null) => void;
    focusBand?: ColorFocusRange | null;
    onFocusBandChange?: (values: [number, number] | null) => void;
    lowColor?: string;
    highColor?: string;
    colors?: readonly string[];
    COLOR_PALETTES?: Array<{ label: string; colors?: readonly string[] }>;
    onCustomColorsChange?: (colors: string[] | undefined) => void;
    invert?: boolean;
    onInvertChange?: (value: boolean) => void;
    transparencyPercent?: number;
    onTransparencyChange?: (val: number | undefined) => void;
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
    glucose ? ["Point 1", "Point 2", "Point 3", "Point 4"] : ["minimum", "maximum"]
  );
  // A straight low/high mix has no meaningful middle bands, so a custom palette only exposes the two ends.
  const usesCustomPalette = $derived(glucose && !!lowColor && !!highColor);
  // The two hidden middle points still need valid values; keep them evenly spaced between low and high.
  const sliderValues = $derived(usesCustomPalette ? [values[0], values[3]] : values);
  const inputStep = $derived(glucose ? (units === "mmol" ? 0.1 : 1) : "any");
  const gradient = $derived(
    glucose
      ? `linear-gradient(to right in srgb, ${stops.map((stop) => `${stop.color} ${((stop.mgdl - minimum) / (maximum - minimum)) * 100}%`).join(", ")})`
      : colorFocusGradient(resolveColorFocusRange(values)!, maximum, cssVar, lowColor, highColor, invert, colors)
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
    insertSliderSteps(baseSliderSteps, glucose ? sliderValues : [...values, maximum])
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
  let dimDraft = $state<number | undefined>(undefined);
  let invalidBound = $state<number | null>(null);
  let invalidFocusBound = $state<number | null>(null);
  let invalidDim = $state(false);

  const display = (value: number) =>
    glucose ? convertToDisplayUnits(value, units) : value;
  const formatted = (value: number) =>
    glucose ? formatGlucoseValue(value, units) : String(value);
  const accessibleLabel = (index: number) =>
    usesCustomPalette
      ? `${metricLabel} ${index === 0 ? "low" : "high"} color point`
      : `${metricLabel} ${labels[index]} color ${glucose ? "boundary" : "value"}`;

  $effect(() => {
    drafts = values.map(display);
    invalidBound = null;
    focusDrafts = focusBandValues.map(display);
    invalidFocusBound = null;
    dimDraft = transparencyPercent;
    invalidDim = false;
  });

  function expandCustomPaletteBounds(low: number, high: number): number[] {
    const step = (high - low) / 3;
    return [low, low + step, low + 2 * step, high];
  }

  function change(candidate: number[]): boolean {
    if (glucose) {
      const next = resolveGlucoseColorThresholds(
        usesCustomPalette && candidate.length === 2
          ? expandCustomPaletteBounds(candidate[0], candidate[1])
          : candidate
      );
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

  function changeCustomPaletteBound(
    edge: 0 | 1,
    event: Event & { currentTarget: HTMLInputElement }
  ) {
    const index = edge === 0 ? 0 : 3;
    const input = event.currentTarget;
    if (!input.value || !Number.isFinite(input.valueAsNumber)) {
      invalidBound = index;
      return;
    }
    if (input.valueAsNumber === display(values[index])) {
      invalidBound = null;
      return;
    }
    const raw = convertFromDisplayUnits(input.valueAsNumber, units);
    const low = edge === 0 ? raw : values[0];
    const high = edge === 1 ? raw : values[3];
    invalidBound = change(expandCustomPaletteBounds(low, high)) ? null : index;
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

  function changeDim(event: Event & { currentTarget: HTMLInputElement }) {
    const input = event.currentTarget;
    if (
      !input.value ||
      !Number.isFinite(input.valueAsNumber) ||
      input.valueAsNumber < 0 ||
      input.valueAsNumber > 100
    ) {
      invalidDim = true;
      return;
    }
    invalidDim = false;
    onTransparencyChange?.(input.valueAsNumber);
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
    onInvertChange?.(false);
    invalidBound = null;
    invalidFocusBound = null;
  }
</script>

<div
  class="color-focus w-full max-w-[440px] min-w-0 text-xs text-muted-foreground space-y-3"
  data-testid={glucose ? "glucose-color-focus" : "color-focus"}
>
  <div class="print:hidden space-y-2">
    <!-- SLIDER BAR (Strictly identical width across all views) -->
    <div class="space-y-1">
      <div class="relative flex h-10 w-full items-center">
        <!-- 1. Focus lines (transparency boundary) -->
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
                class="relative z-[5] pointer-events-auto block w-2.5 h-7 -mt-0.5 rounded-sm border-2 border-foreground bg-background shadow-md cursor-ew-resize before:absolute before:-inset-3 focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-ring/50"
              />
            {/each}
          {/snippet}
        </Slider.Root>

        <!-- 2. Color gradient & color scaling bullets stay above focus lines. -->
        <Slider.Root
          type="multiple"
          min={minimum}
          max={maximum}
          step={sliderSteps}
          autoSort={false}
          thumbPositioning="exact"
          bind:value={
            () => [...sliderValues],
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
                class="relative z-10 block size-5 shrink-0 rounded-full border-2 border-foreground bg-background shadow-sm before:absolute before:-inset-3 focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-ring/50 cursor-pointer"
              />
            {/each}
          {/snippet}
        </Slider.Root>
      </div>

      <div class="flex justify-between tabular-nums text-[11px]" aria-hidden="true">
        <span>{formatted(minimum)} {unitLabel}</span>
        <span>{formatted(maximum)} {unitLabel}</span>
      </div>
    </div>

    <!-- Color Boundaries / Focus Window cards, shared layout for every metric. -->
    {#if metricKey === "avgGlucose"}
      <div class="space-y-2 pt-1">
        <div class="grid grid-cols-2 gap-2">
          <div class="p-2 rounded border border-border/60 bg-muted/20 space-y-1.5">
            <div class="text-[11px] font-medium text-foreground/80 flex items-center gap-1">
              <span class="size-2.5 rounded-full bg-primary/70"></span>
              Color Boundaries
            </div>
            <div class="grid grid-cols-2 gap-1.5">
              {#if usesCustomPalette}
                <div class="min-w-0">
                  <label for={id + "-bound-0"} class="mb-0.5 flex items-center gap-1 text-muted-foreground text-[10px] truncate">
                    <span class="inline-block size-2 shrink-0 rounded-full" style:background={getGlucoseHeatmapFill(values[0], stops)}></span>
                    Low
                  </label>
                  <Input
                    id={id + "-bound-0"}
                    type="number"
                    inputmode="decimal"
                    min={display(minimum)}
                    max={display(maximum)}
                    step={inputStep}
                    bind:value={drafts[0]}
                    oninput={(event: Event & { currentTarget: HTMLInputElement }) =>
                      changeCustomPaletteBound(0, event)}
                    aria-label={accessibleLabel(0)}
                    aria-invalid={invalidBound === 0}
                    size="xs"
                    class="w-full tabular-nums"
                  />
                </div>
                <div class="min-w-0">
                  <label for={id + "-bound-3"} class="mb-0.5 flex items-center gap-1 text-muted-foreground text-[10px] truncate">
                    <span class="inline-block size-2 shrink-0 rounded-full" style:background={getGlucoseHeatmapFill(values[3], stops)}></span>
                    High
                  </label>
                  <Input
                    id={id + "-bound-3"}
                    type="number"
                    inputmode="decimal"
                    min={display(minimum)}
                    max={display(maximum)}
                    step={inputStep}
                    bind:value={drafts[3]}
                    oninput={(event: Event & { currentTarget: HTMLInputElement }) =>
                      changeCustomPaletteBound(1, event)}
                    aria-label={accessibleLabel(1)}
                    aria-invalid={invalidBound === 3}
                    size="xs"
                    class="w-full tabular-nums"
                  />
                </div>
              {:else}
                {#each labels as label, index}
                  <div class="min-w-0">
                    <label for={id + "-bound-" + index} class="mb-0.5 flex items-center gap-1 text-muted-foreground text-[10px] truncate">
                      <span class="inline-block size-2 shrink-0 rounded-full" style:background={getGlucoseHeatmapFill(values[index], stops)}></span>
                      {label}
                    </label>
                    <Input
                      id={id + "-bound-" + index}
                      type="number"
                      inputmode="decimal"
                      min={display(minimum)}
                      max={display(maximum)}
                      step={inputStep}
                      bind:value={drafts[index]}
                      oninput={(event: Event & { currentTarget: HTMLInputElement }) =>
                        changeBound(index, event)}
                      aria-label={accessibleLabel(index)}
                      aria-invalid={invalidBound === index}
                      size="xs"
                      class="w-full tabular-nums"
                    />
                  </div>
                {/each}
              {/if}
            </div>
          </div>

          <div class="p-2 rounded border border-border/60 bg-muted/20 space-y-1.5">
            <div class="text-[11px] font-medium text-foreground/80 flex items-center gap-1">
              <span class="w-1.5 h-3 rounded-sm bg-foreground/70"></span>
              Focus Window
            </div>
            <div class="flex items-center gap-1.5">
              <Input
                type="number"
                inputmode="decimal"
                min={display(minimum)}
                max={display(maximum)}
                step={inputStep}
                bind:value={focusDrafts[0]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(0, e)}
                aria-label={`${metricLabel} focus minimum value`}
                size="xs"
                class="w-16 tabular-nums"
              />
              <span class="text-muted-foreground text-xs">→</span>
              <Input
                type="number"
                inputmode="decimal"
                min={display(minimum)}
                max={display(maximum)}
                step={inputStep}
                bind:value={focusDrafts[1]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(1, e)}
                aria-label={`${metricLabel} focus maximum value`}
                size="xs"
                class="w-16 tabular-nums"
              />
              <span class="text-muted-foreground text-[11px]">{unitLabel}</span>
            </div>
            <div class="flex items-center gap-1.5 pt-1 mt-1 border-t border-border/30">
              <span class="text-[10px] text-muted-foreground">Dim:</span>
              <Input
                type="number"
                min={0}
                max={100}
                bind:value={dimDraft}
                oninput={changeDim}
                aria-invalid={invalidDim}
                size="xs"
                class="w-12 tabular-nums"
              />
              <span class="text-[10px] text-muted-foreground">%</span>
            </div>
          </div>
        </div>

        <div class="flex flex-wrap items-center justify-between gap-2 p-2 rounded bg-muted/30 border border-border/40">
          <div class="flex items-center gap-2 flex-wrap" aria-label="Color palette presets">
            {#each COLOR_PALETTES as pal}
              {@const isSelected = (colors?.join(",") ?? "") === (pal.colors?.join(",") ?? "")}
              {@const themeColors = (themeStops ?? stops).map((stop) => stop.color)}
              <button
                type="button"
                title={pal.label}
                aria-label={pal.label + " palette"}
                aria-pressed={isSelected}
                class="size-6 rounded-full border border-border shadow-sm transition-transform {isSelected ? 'scale-110 ring-2 ring-primary ring-offset-1 ring-offset-background' : 'hover:scale-105'}"
                style:background={paletteSwatchGradient(pal.colors ?? themeColors)}
                onclick={() => onCustomColorsChange?.(pal.colors ? [...pal.colors] : undefined)}
              ></button>
            {/each}
          </div>
          <div class="flex items-center gap-1.5">
            <Button
              variant="outline"
              size="xs"
              aria-pressed={invert}
              disabled={!usesCustomPalette}
              title={usesCustomPalette ? undefined : "Theme colors are fixed; pick a palette to invert"}
              onclick={() => onInvertChange?.(!invert)}
            >Invert</Button>
            <Button variant="outline" size="xs" aria-label={resetLabel} onclick={reset}>Reset</Button>
          </div>
        </div>
      </div>

    {:else}
      <div class="space-y-2 pt-1">
        <div class="grid grid-cols-2 gap-2">
          <div class="p-2 rounded border border-border/60 bg-muted/20 space-y-1.5">
            <div class="text-[11px] font-medium text-foreground/80 flex items-center gap-1">
              <span class="size-2.5 rounded-full bg-primary/70"></span>
              Color Range
            </div>
            <div class="flex items-center gap-1.5">
              <Input
                type="number"
                min={0}
                max={fixedMax}
                step={inputStep}
                bind:value={drafts[0]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeBound(0, e)}
                size="xs"
                class="w-16 tabular-nums"
              />
              <span class="text-muted-foreground text-xs">→</span>
              <Input
                type="number"
                min={0}
                max={fixedMax}
                step={inputStep}
                bind:value={drafts[1]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeBound(1, e)}
                size="xs"
                class="w-16 tabular-nums"
              />
              <span class="text-muted-foreground text-[11px]">{unitLabel}</span>
            </div>
          </div>

          <div class="p-2 rounded border border-border/60 bg-muted/20 space-y-1.5">
            <div class="text-[11px] font-medium text-foreground/80 flex items-center gap-1">
              <span class="w-1.5 h-3 rounded-sm bg-foreground/70"></span>
              Focus Window
            </div>
            <div class="flex items-center gap-1.5">
              <Input
                type="number"
                min={0}
                max={fixedMax}
                step={inputStep}
                bind:value={focusDrafts[0]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(0, e)}
                size="xs"
                class="w-16 tabular-nums"
              />
              <span class="text-muted-foreground text-xs">→</span>
              <Input
                type="number"
                min={0}
                max={fixedMax}
                step={inputStep}
                bind:value={focusDrafts[1]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(1, e)}
                size="xs"
                class="w-16 tabular-nums"
              />
              <span class="text-muted-foreground text-[11px]">{unitLabel}</span>
            </div>
            <div class="flex items-center gap-1.5 pt-1 mt-1 border-t border-border/30">
              <span class="text-[10px] text-muted-foreground">Dim:</span>
              <Input
                type="number"
                min={0}
                max={100}
                bind:value={dimDraft}
                oninput={changeDim}
                aria-invalid={invalidDim}
                size="xs"
                class="w-12 tabular-nums"
              />
              <span class="text-[10px] text-muted-foreground">%</span>
            </div>
          </div>
        </div>

        <div class="flex flex-wrap items-center justify-between gap-2 p-2 rounded bg-muted/30 border border-border/40">
          <div class="flex items-center gap-2 flex-wrap" aria-label="Color palette presets">
            {#each COLOR_PALETTES as pal}
              {@const isSelected = (colors?.join(",") ?? "") === (pal.colors?.join(",") ?? "")}
              <button
                type="button"
                title={pal.label}
                aria-label={pal.label + " palette"}
                aria-pressed={isSelected}
                class="size-6 rounded-full border border-border shadow-sm transition-transform {isSelected ? 'scale-110 ring-2 ring-primary ring-offset-1 ring-offset-background' : 'hover:scale-105'}"
                style:background={pal.colors ? paletteSwatchGradient(pal.colors) : `linear-gradient(135deg, color-mix(in srgb, var(${cssVar}) 18%, transparent), var(${cssVar}))`}
                onclick={() => onCustomColorsChange?.(pal.colors ? [...pal.colors] : undefined)}
              ></button>
            {/each}
          </div>
          <div class="flex items-center gap-1.5">
            <Button variant="outline" size="xs" aria-pressed={invert} onclick={() => onInvertChange?.(!invert)}>Invert</Button>
            <Button variant="outline" size="xs" aria-label={resetLabel} onclick={reset}>Reset</Button>
          </div>
        </div>
      </div>
    {/if}

    <!-- Validation error alert -->
    {#if invalidBound !== null || invalidFocusBound !== null || invalidDim}
      <p id={id + "-error"} class="text-destructive text-[11px] pt-1" role="alert">
        {#if invalidDim}
          Enter a dim percentage between 0 and 100.
        {:else if glucose}
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
