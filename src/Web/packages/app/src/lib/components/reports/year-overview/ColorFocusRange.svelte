<script lang="ts">
  import { Slider } from "bits-ui";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import {
    colorFocusGradient,
    resolveColorFocusRange,
    type ColorFocusRange,
  } from "$lib/utils/metric-color-focus";

  let {
    metricLabel,
    unit,
    observedMax,
    cssVar,
    fixedMax,
    focusRange = null,
    onFocusRangeChange,
  }: {
    metricLabel: string;
    unit: string;
    observedMax: number;
    cssVar: string;
    fixedMax?: number;
    focusRange?: ColorFocusRange | null;
    onFocusRangeChange: (range: [number, number] | null) => void;
  } = $props();

  const id = $props.id();
  const automaticMax = $derived(
    fixedMax ??
      (Number.isFinite(observedMax) && observedMax > 0 ? observedMax : 1)
  );
  const range = $derived(focusRange ?? ([0, automaticMax] as const));
  const domainMax = $derived(fixedMax ?? Math.max(automaticMax, range[1], 1));
  const gradient = $derived(colorFocusGradient(range, domainMax, cssVar));
  const baseSliderSteps = $derived.by(() => {
    const step = Math.max(0.1, domainMax / 10_000);
    const count = Math.min(10_000, Math.floor(domainMax / step));
    return Array.from({ length: count + 1 }, (_, index) =>
      Number((index * step).toPrecision(12))
    );
  });
  // Bits UI normalizes supplied values to its steps, including untouched Auto values.
  const sliderSteps = $derived.by(() =>
    [...new Set([...baseSliderSteps, range[0], range[1], domainMax])].sort(
      (left, right) => left - right
    )
  );
  let minimumDraft = $state<number | undefined>();
  let maximumDraft = $state<number | undefined>();
  let invalidBound = $state<0 | 1 | null>(null);

  $effect(() => {
    void metricLabel;
    minimumDraft = range[0];
    maximumDraft = range[1];
    invalidBound = null;
  });

  function changeSlider(values: number[]) {
    const next = resolveColorFocusRange(values);
    if (!next || next[1] > domainMax) return;
    onFocusRangeChange([next[0], next[1]]);
  }

  function changeBound(index: 0 | 1, input: HTMLInputElement) {
    const value = input.valueAsNumber;
    const next = resolveColorFocusRange(
      index === 0 ? [value, range[1]] : [range[0], value]
    );
    if (
      !input.value ||
      !next ||
      (fixedMax !== undefined && next[1] > fixedMax)
    ) {
      invalidBound = index;
      return;
    }
    invalidBound = null;
    onFocusRangeChange([next[0], next[1]]);
  }

  function resetRange() {
    minimumDraft = 0;
    maximumDraft = automaticMax;
    invalidBound = null;
    onFocusRangeChange(null);
  }
</script>

<div
  class="w-full max-w-[420px] min-w-0 text-xs text-muted-foreground color-focus"
>
  <div class="print:hidden">
    <Slider.Root
      type="multiple"
      min={0}
      max={domainMax}
      step={sliderSteps}
      autoSort={false}
      thumbPositioning="exact"
      bind:value={() => [range[0], range[1]], changeSlider}
      class="relative flex h-10 w-full touch-none select-none items-center"
      aria-label="{metricLabel} color focus"
    >
      {#snippet children({ thumbItems })}
        <span
          class="h-3.5 w-full rounded-sm"
          style:background={gradient}
          data-color-focus-track
        ></span>
        {#each thumbItems as thumb (thumb.index)}
          <Slider.Thumb
            index={thumb.index}
            aria-label="{metricLabel} {thumb.index === 0
              ? 'minimum'
              : 'maximum'} color value"
            aria-valuetext="{thumb.value} {unit}"
            class="block size-5 shrink-0 rounded-full border-2 border-foreground bg-background shadow-sm before:absolute before:-inset-3 focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-ring/50"
          />
        {/each}
      {/snippet}
    </Slider.Root>
    <div class="mb-2 flex justify-between tabular-nums" aria-hidden="true">
      <span>0 {unit}</span>
      <span>{domainMax} {unit}</span>
    </div>
    <div class="flex flex-wrap items-center gap-x-2 gap-y-2">
      <label for="{id}-minimum">Min</label>
      <Input
        id="{id}-minimum"
        type="number"
        inputmode="decimal"
        min={0}
        max={range[1]}
        step="any"
        bind:value={minimumDraft}
        oninput={(event) => changeBound(0, event.currentTarget)}
        aria-label="{metricLabel} minimum color value"
        aria-invalid={invalidBound === 0}
        aria-describedby={invalidBound === 0 ? `${id}-error` : undefined}
        class="h-8 w-20 px-2 text-xs tabular-nums"
      />
      <label for="{id}-maximum">Max</label>
      <Input
        id="{id}-maximum"
        type="number"
        inputmode="decimal"
        min={range[0]}
        max={fixedMax}
        step="any"
        bind:value={maximumDraft}
        oninput={(event) => changeBound(1, event.currentTarget)}
        aria-label="{metricLabel} maximum color value"
        aria-invalid={invalidBound === 1}
        aria-describedby={invalidBound === 1 ? `${id}-error` : undefined}
        class="h-8 w-20 px-2 text-xs tabular-nums"
      />
      <span>{unit}</span>
      <Button
        variant="outline"
        size="sm"
        class="h-8 px-2 text-xs"
        aria-label="Reset {metricLabel} color range to automatic"
        onclick={resetRange}
      >
        Auto
      </Button>
    </div>
    {#if invalidBound !== null}
      <p id="{id}-error" class="mt-2 text-destructive" role="alert">
        Enter a minimum of 0 or more and a maximum greater than the minimum{fixedMax !==
        undefined
          ? `, up to ${fixedMax} ${unit}`
          : ""}.
      </p>
    {/if}
    <p class="mt-2">Values outside the selected range use the end colors.</p>
  </div>
  <div class="hidden print:block">
    <div class="h-3.5 w-full rounded-sm" style:background={gradient}></div>
    <div class="mt-1 flex justify-between tabular-nums">
      <span>0 {unit}</span>
      <span>{domainMax} {unit}</span>
    </div>
    <p class="mt-1">
      {metricLabel}: color focus {range[0]}–{range[1]}
      {unit}; values outside use the end colors.
    </p>
  </div>
</div>

<style>
  .color-focus {
    print-color-adjust: exact;
    -webkit-print-color-adjust: exact;
  }
</style>
