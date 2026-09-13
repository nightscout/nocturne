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
    onThresholdsChange = () => {},
    focusBand = null,
    onFocusBandChange = () => {},
    lowColor = undefined,
    highColor = undefined,
    COLOR_PALETTES = [],
    onCustomColorsChange = () => {},
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
    onThresholdsChange?: (values: GlucoseColorThresholds | null) => void;
    focusBand?: ColorFocusRange | null;
    onFocusBandChange?: (values: [number, number] | null) => void;
    lowColor?: string;
    highColor?: string;
    COLOR_PALETTES?: Array<{ label: string; low?: string; high?: string }>;
    onCustomColorsChange?: (low?: string, high?: string) => void;
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
  class="color-focus w-full max-w-[440px] min-w-0 text-xs text-muted-foreground space-y-3"
  data-testid={glucose ? "glucose-color-focus" : "color-focus"}
>
  <div class="print:hidden space-y-2">
    <!-- SLIDER BAR (Strictly identical width across all views) -->
    <div class="space-y-1">
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

        <!-- 2. Focus lines (transparency boundary) -->
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
    </div>

    <!-- ========================================================================= -->
    <!-- VARIANT 1: Avg Glucose — "Classic Polished Card"                         -->
    <!-- ========================================================================= -->
    {#if metricKey === "avgGlucose"}
      <div class="space-y-2.5 pt-1">
        <!-- 4 Glucose thresholds -->
        <div class="grid grid-cols-2 gap-2 sm:grid-cols-4">
          {#each labels as label, index}
            <div class="min-w-0">
              <label for={id + "-bound-" + index} class="mb-1 block text-muted-foreground text-[11px]">
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
                class="h-7 w-full px-2 text-xs tabular-nums"
              />
            </div>
          {/each}
        </div>

        <!-- Focus lines & Transparency row -->
        <div class="p-2 rounded-md bg-muted/30 border border-border/40 space-y-2">
          <div class="flex items-center justify-between gap-2">
            <span class="font-medium text-foreground/80 text-[11px]">Focus Lines:</span>
            <div class="flex items-center gap-2">
              <div class="flex items-center gap-1">
                <span class="text-[11px] text-muted-foreground">Min:</span>
                <Input
                  type="number"
                  inputmode="decimal"
                  min={display(minimum)}
                  max={display(maximum)}
                  step={inputStep}
                  bind:value={focusDrafts[0]}
                  oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(0, e)}
                  class="h-6 w-16 px-1.5 text-xs tabular-nums"
                />
              </div>
              <div class="flex items-center gap-1">
                <span class="text-[11px] text-muted-foreground">Max:</span>
                <Input
                  type="number"
                  inputmode="decimal"
                  min={display(minimum)}
                  max={display(maximum)}
                  step={inputStep}
                  bind:value={focusDrafts[1]}
                  oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(1, e)}
                  class="h-6 w-16 px-1.5 text-xs tabular-nums"
                />
              </div>
              <Button variant="ghost" size="sm" class="h-6 px-1.5 text-[11px]" onclick={resetFocusBand}>
                Reset
              </Button>
            </div>
          </div>

          <div class="flex items-center justify-between gap-2 border-t border-border/30 pt-1.5">
            <div class="flex items-center gap-1.5">
              <span class="text-muted-foreground text-[11px]">Transparency:</span>
              <Input
                type="number"
                min={0}
                max={100}
                step={1}
                value={transparencyPercent}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => onTransparencyChange?.(e.currentTarget.valueAsNumber)}
                class="h-6 w-14 px-1 text-xs tabular-nums"
              />
              <span class="text-muted-foreground text-[11px]">%</span>
            </div>
            <Button variant="outline" size="sm" class="h-6 px-2 text-[11px]" onclick={reset}>
              Reset all
            </Button>
          </div>
        </div>
      </div>

    <!-- ========================================================================= -->
    <!-- VARIANT 2: TIR — "Card Deck / Grouped Sections"                           -->
    <!-- ========================================================================= -->
    {:else if metricKey === "tir"}
      <div class="space-y-2 pt-1">
        <div class="grid grid-cols-2 gap-2">
          <!-- Card A: Color limits -->
          <div class="p-2 rounded border border-border/60 bg-muted/20 space-y-1.5">
            <div class="text-[11px] font-medium text-foreground/80 flex items-center gap-1">
              <span>🎨</span> Color Range
            </div>
            <div class="flex items-center gap-1.5">
              <Input
                type="number"
                min={0}
                max={fixedMax}
                step={inputStep}
                bind:value={drafts[0]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeBound(0, e)}
                class="h-7 w-16 px-1.5 text-xs tabular-nums"
              />
              <span class="text-muted-foreground text-xs">→</span>
              <Input
                type="number"
                min={0}
                max={fixedMax}
                step={inputStep}
                bind:value={drafts[1]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeBound(1, e)}
                class="h-7 w-16 px-1.5 text-xs tabular-nums"
              />
              <span class="text-muted-foreground text-[11px]">{unitLabel}</span>
            </div>
          </div>

          <!-- Card B: Focus window -->
          <div class="p-2 rounded border border-border/60 bg-muted/20 space-y-1.5">
            <div class="text-[11px] font-medium text-foreground/80 flex items-center gap-1">
              <span>🎯</span> Focus Window
            </div>
            <div class="flex items-center gap-1.5">
              <Input
                type="number"
                min={0}
                max={fixedMax}
                step={inputStep}
                bind:value={focusDrafts[0]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(0, e)}
                class="h-7 w-16 px-1.5 text-xs tabular-nums"
              />
              <span class="text-muted-foreground text-xs">→</span>
              <Input
                type="number"
                min={0}
                max={fixedMax}
                step={inputStep}
                bind:value={focusDrafts[1]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(1, e)}
                class="h-7 w-16 px-1.5 text-xs tabular-nums"
              />
              <span class="text-muted-foreground text-[11px]">{unitLabel}</span>
            </div>
          </div>
        </div>

        <!-- Palettes & Transparency footer -->
        <div class="flex flex-wrap items-center justify-between gap-2 p-2 rounded bg-muted/30 border border-border/40">
          <div class="flex items-center gap-1 flex-wrap">
            {#each COLOR_PALETTES.slice(0, 4) as pal}
              <Button
                variant={lowColor === pal.low && highColor === pal.high ? "secondary" : "ghost"}
                size="sm"
                class="h-6 px-1.5 text-[10px] gap-1"
                onclick={() => onCustomColorsChange?.(pal.low, pal.high)}
              >
                {#if pal.low}
                  <span class="size-2.5 rounded-full" style:background="linear-gradient(to right, {pal.low}, {pal.high})"></span>
                {/if}
                {pal.label.split(" ")[0]}
              </Button>
            {/each}
          </div>
          <div class="flex items-center gap-1.5">
            <span class="text-[10px] text-muted-foreground">Dim:</span>
            <Input
              type="number"
              min={0}
              max={100}
              value={transparencyPercent}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => onTransparencyChange?.(e.currentTarget.valueAsNumber)}
              class="h-6 w-12 px-1 text-[11px] tabular-nums"
            />
            <span class="text-[10px] text-muted-foreground">%</span>
            <Button variant="outline" size="sm" class="h-6 px-2 text-[11px]" onclick={reset}>Auto</Button>
          </div>
        </div>
      </div>

    <!-- ========================================================================= -->
    <!-- VARIANT 3: Bolus — "Compact Row-Pair Table Strip"                         -->
    <!-- ========================================================================= -->
    {:else if metricKey === "bolus"}
      <div class="space-y-1.5 pt-1 text-[11px]">
        <!-- Row 1: Color Ramp -->
        <div class="flex items-center justify-between p-1.5 rounded bg-muted/20 border border-border/40 gap-2">
          <div class="flex items-center gap-1.5">
            <span class="font-medium text-foreground/80 w-16">🎨 Scale:</span>
            <Input
              type="number"
              min={0}
              max={fixedMax}
              step={inputStep}
              bind:value={drafts[0]}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeBound(0, e)}
              class="h-6 w-14 px-1 text-xs tabular-nums"
            />
            <span>→</span>
            <Input
              type="number"
              min={0}
              max={fixedMax}
              step={inputStep}
              bind:value={drafts[1]}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeBound(1, e)}
              class="h-6 w-14 px-1 text-xs tabular-nums"
            />
            <span>{unitLabel}</span>
          </div>
          <div class="flex items-center gap-1">
            {#each COLOR_PALETTES.slice(1, 4) as pal}
              <button
                type="button"
                title={pal.label}
                class="size-4 rounded-full border border-black/20 {lowColor === pal.low ? 'ring-2 ring-primary ring-offset-1' : ''}"
                style:background="linear-gradient(to right, {pal.low}, {pal.high})"
                onclick={() => onCustomColorsChange?.(pal.low, pal.high)}
              ></button>
            {/each}
          </div>
        </div>

        <!-- Row 2: Focus Lines -->
        <div class="flex items-center justify-between p-1.5 rounded bg-muted/20 border border-border/40 gap-2">
          <div class="flex items-center gap-1.5">
            <span class="font-medium text-foreground/80 w-16">🎯 Focus:</span>
            <Input
              type="number"
              min={0}
              max={fixedMax}
              step={inputStep}
              bind:value={focusDrafts[0]}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(0, e)}
              class="h-6 w-14 px-1 text-xs tabular-nums"
            />
            <span>→</span>
            <Input
              type="number"
              min={0}
              max={fixedMax}
              step={inputStep}
              bind:value={focusDrafts[1]}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(1, e)}
              class="h-6 w-14 px-1 text-xs tabular-nums"
            />
            <span>{unitLabel}</span>
          </div>
          <div class="flex items-center gap-1">
            <span class="text-muted-foreground">Dim:</span>
            <Input
              type="number"
              min={0}
              max={100}
              value={transparencyPercent}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => onTransparencyChange?.(e.currentTarget.valueAsNumber)}
              class="h-6 w-12 px-1 text-xs tabular-nums"
            />
            <span>%</span>
          </div>
        </div>

        <div class="flex justify-end pt-0.5">
          <Button variant="outline" size="sm" class="h-6 px-2 text-[11px]" onclick={reset}>Auto</Button>
        </div>
      </div>

    <!-- ========================================================================= -->
    <!-- VARIANT 4: Basal — "Segmented Tabbed Inspector"                           -->
    <!-- ========================================================================= -->
    {:else if metricKey === "basal"}
      <div class="space-y-2 pt-1 text-xs">
        <!-- Segmented Tab bar -->
        <div class="flex rounded-md bg-muted/60 p-0.5 border border-border/40 text-[11px]">
          <button
            type="button"
            class="flex-1 py-1 rounded font-medium transition-colors {activeTab === 'focus' ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}"
            onclick={() => activeTab = 'focus'}
          >
            🎯 Focus Lines
          </button>
          <button
            type="button"
            class="flex-1 py-1 rounded font-medium transition-colors {activeTab === 'colors' ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}"
            onclick={() => activeTab = 'colors'}
          >
            🎨 Colors & Scale
          </button>
          <button
            type="button"
            class="flex-1 py-1 rounded font-medium transition-colors {activeTab === 'settings' ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}"
            onclick={() => activeTab = 'settings'}
          >
            ⚙️ Transparency
          </button>
        </div>

        <!-- Tab content -->
        <div class="p-2.5 rounded-md border border-border/40 bg-card space-y-2">
          {#if activeTab === 'focus'}
            <div class="flex items-center justify-between gap-2">
              <span class="text-[11px] font-medium">Focus cutoffs:</span>
              <div class="flex items-center gap-1.5">
                <span class="text-[11px] text-muted-foreground">Min:</span>
                <Input
                  type="number"
                  min={0}
                  max={fixedMax}
                  step={inputStep}
                  bind:value={focusDrafts[0]}
                  oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(0, e)}
                  class="h-7 w-16 px-1.5 text-xs tabular-nums"
                />
                <span class="text-[11px] text-muted-foreground">Max:</span>
                <Input
                  type="number"
                  min={0}
                  max={fixedMax}
                  step={inputStep}
                  bind:value={focusDrafts[1]}
                  oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(1, e)}
                  class="h-7 w-16 px-1.5 text-xs tabular-nums"
                />
                <Button variant="ghost" size="sm" class="h-7 px-2 text-[11px]" onclick={resetFocusBand}>Reset</Button>
              </div>
            </div>
          {:else if activeTab === 'colors'}
            <div class="space-y-2">
              <div class="flex items-center justify-between gap-2">
                <span class="text-[11px] font-medium">Scale range:</span>
                <div class="flex items-center gap-1.5">
                  <Input
                    type="number"
                    min={0}
                    max={fixedMax}
                    step={inputStep}
                    bind:value={drafts[0]}
                    oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeBound(0, e)}
                    class="h-7 w-16 px-1.5 text-xs tabular-nums"
                  />
                  <span>→</span>
                  <Input
                    type="number"
                    min={0}
                    max={fixedMax}
                    step={inputStep}
                    bind:value={drafts[1]}
                    oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeBound(1, e)}
                    class="h-7 w-16 px-1.5 text-xs tabular-nums"
                  />
                  <span>{unitLabel}</span>
                </div>
              </div>
              <div class="flex items-center gap-1.5 pt-1 border-t border-border/30 flex-wrap">
                {#each COLOR_PALETTES.slice(0, 5) as pal}
                  <Button
                    variant={lowColor === pal.low ? "secondary" : "outline"}
                    size="sm"
                    class="h-6 px-1.5 text-[10px] gap-1"
                    onclick={() => onCustomColorsChange?.(pal.low, pal.high)}
                  >
                    {#if pal.low}
                      <span class="size-2 rounded-full" style:background="linear-gradient(to right, {pal.low}, {pal.high})"></span>
                    {/if}
                    {pal.label.split(" ")[0]}
                  </Button>
                {/each}
              </div>
            </div>
          {:else}
            <div class="flex items-center justify-between">
              <span class="text-[11px] font-medium">Out-of-band dimming:</span>
              <div class="flex items-center gap-1">
                <Input
                  type="number"
                  min={0}
                  max={100}
                  value={transparencyPercent}
                  oninput={(e: Event & { currentTarget: HTMLInputElement }) => onTransparencyChange?.(e.currentTarget.valueAsNumber)}
                  class="h-7 w-16 px-1.5 text-xs tabular-nums"
                />
                <span class="text-muted-foreground text-xs">%</span>
              </div>
            </div>
          {/if}
        </div>
      </div>

    <!-- ========================================================================= -->
    <!-- VARIANT 5: TDD — "Figma-Style Inline Input Badges & Swatches"               -->
    <!-- ========================================================================= -->
    {:else if metricKey === "tdd"}
      <div class="space-y-2 pt-1 text-xs">
        <div class="grid grid-cols-2 gap-2">
          <!-- Scale limits badge -->
          <div class="flex items-center rounded-md border border-border bg-muted/20 px-2 py-1 justify-between">
            <span class="text-[11px] text-muted-foreground">Scale:</span>
            <div class="flex items-center gap-1">
              <Input
                type="number"
                min={0}
                max={fixedMax}
                step={inputStep}
                bind:value={drafts[0]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeBound(0, e)}
                class="h-6 w-12 px-1 text-xs border-0 bg-transparent text-right font-mono"
              />
              <span class="text-muted-foreground text-[10px]">..</span>
              <Input
                type="number"
                min={0}
                max={fixedMax}
                step={inputStep}
                bind:value={drafts[1]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeBound(1, e)}
                class="h-6 w-12 px-1 text-xs border-0 bg-transparent font-mono"
              />
              <span class="text-[10px] text-muted-foreground">{unitLabel}</span>
            </div>
          </div>

          <!-- Focus limits badge -->
          <div class="flex items-center rounded-md border border-border bg-muted/20 px-2 py-1 justify-between">
            <span class="text-[11px] text-muted-foreground">Focus:</span>
            <div class="flex items-center gap-1">
              <Input
                type="number"
                min={0}
                max={fixedMax}
                step={inputStep}
                bind:value={focusDrafts[0]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(0, e)}
                class="h-6 w-12 px-1 text-xs border-0 bg-transparent text-right font-mono"
              />
              <span class="text-muted-foreground text-[10px]">..</span>
              <Input
                type="number"
                min={0}
                max={fixedMax}
                step={inputStep}
                bind:value={focusDrafts[1]}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(1, e)}
                class="h-6 w-12 px-1 text-xs border-0 bg-transparent font-mono"
              />
              <span class="text-[10px] text-muted-foreground">{unitLabel}</span>
            </div>
          </div>
        </div>

        <!-- Palette Swatch strip & Transparency stepper -->
        <div class="flex items-center justify-between pt-1 border-t border-border/40">
          <div class="flex items-center gap-1.5">
            {#each COLOR_PALETTES as pal}
              <button
                type="button"
                title={pal.label}
                class="size-5 rounded-full border border-black/20 transition-transform {lowColor === pal.low ? 'scale-125 ring-2 ring-primary ring-offset-1' : 'hover:scale-110'}"
                style:background={pal.low ? `linear-gradient(to right, ${pal.low}, ${pal.high})` : "var(--primary)"}
                onclick={() => onCustomColorsChange?.(pal.low, pal.high)}
              ></button>
            {/each}
          </div>
          <div class="flex items-center gap-1.5">
            <span class="text-[10px] text-muted-foreground">Dim:</span>
            <Input
              type="number"
              min={0}
              max={100}
              value={transparencyPercent}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => onTransparencyChange?.(e.currentTarget.valueAsNumber)}
              class="h-6 w-12 px-1 text-xs tabular-nums"
            />
            <span class="text-[10px] text-muted-foreground">%</span>
            <Button variant="ghost" size="sm" class="h-6 px-1.5 text-[11px]" onclick={reset}>Auto</Button>
          </div>
        </div>
      </div>

    <!-- ========================================================================= -->
    <!-- VARIANT 6: Carbs — "Minimalist Stacked Flow"                              -->
    <!-- ========================================================================= -->
    {:else}
      <div class="space-y-2 pt-1 text-xs">
        <div class="flex items-center justify-between gap-2 p-2 rounded bg-muted/20 border border-border/40">
          <span class="font-medium text-foreground/80 text-[11px]">Scale limits:</span>
          <div class="flex items-center gap-1.5">
            <Input
              type="number"
              min={0}
              max={fixedMax}
              step={inputStep}
              bind:value={drafts[0]}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeBound(0, e)}
              class="h-7 w-16 px-1.5 text-xs tabular-nums"
            />
            <span class="text-muted-foreground">to</span>
            <Input
              type="number"
              min={0}
              max={fixedMax}
              step={inputStep}
              bind:value={drafts[1]}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeBound(1, e)}
              class="h-7 w-16 px-1.5 text-xs tabular-nums"
            />
            <span class="text-muted-foreground text-[11px]">{unitLabel}</span>
          </div>
        </div>

        <div class="flex items-center justify-between gap-2 p-2 rounded bg-muted/20 border border-border/40">
          <span class="font-medium text-foreground/80 text-[11px]">Active window:</span>
          <div class="flex items-center gap-1.5">
            <Input
              type="number"
              min={0}
              max={fixedMax}
              step={inputStep}
              bind:value={focusDrafts[0]}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(0, e)}
              class="h-7 w-16 px-1.5 text-xs tabular-nums"
            />
            <span class="text-muted-foreground">to</span>
            <Input
              type="number"
              min={0}
              max={fixedMax}
              step={inputStep}
              bind:value={focusDrafts[1]}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => changeFocusBound(1, e)}
              class="h-7 w-16 px-1.5 text-xs tabular-nums"
            />
            <span class="text-muted-foreground text-[11px]">{unitLabel}</span>
          </div>
        </div>

        <div class="flex items-center justify-between pt-1 gap-2">
          <div class="flex items-center gap-1">
            {#each COLOR_PALETTES.slice(0, 4) as pal}
              <Button
                variant={lowColor === pal.low ? "secondary" : "outline"}
                size="sm"
                class="h-6 px-1.5 text-[10px]"
                onclick={() => onCustomColorsChange?.(pal.low, pal.high)}
              >
                {pal.label.split(" ")[0]}
              </Button>
            {/each}
          </div>
          <div class="flex items-center gap-1">
            <span class="text-[10px] text-muted-foreground">Fade:</span>
            <Input
              type="number"
              min={0}
              max={100}
              value={transparencyPercent}
              oninput={(e: Event & { currentTarget: HTMLInputElement }) => onTransparencyChange?.(e.currentTarget.valueAsNumber)}
              class="h-6 w-12 px-1 text-xs tabular-nums"
            />
            <span class="text-[10px] text-muted-foreground">%</span>
            <Button variant="ghost" size="sm" class="h-6 px-1.5 text-[11px]" onclick={reset}>Reset</Button>
          </div>
        </div>
      </div>
    {/if}

    <!-- Validation error alert -->
    {#if invalidBound !== null || invalidFocusBound !== null}
      <p id={id + "-error"} class="text-destructive text-[11px] pt-1" role="alert">
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
