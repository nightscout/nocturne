<!--
  One curve's bias by local hour of the prediction and horizon, as a table whose cells are tinted
  by the sign of the bias and the size the API scales it to: one hue for predictions that ran high,
  another for low, fading to the surface at zero. Every cell carries its value, so the tint is never
  read alone.
-->
<script lang="ts">
  import type { PredictionCurveAccuracy, PredictionHorizonAccuracy } from "$lib/api";
  import { hourLabel } from "$lib/utils/formatting";
  import { atMinute } from "./daily-profile-view";
  import { curveLabel, horizonLabel } from "./prediction-labels.svelte";

  interface Props {
    curve: PredictionCurveAccuracy;
    formatError: (mgdl: number) => string;
    unit: string;
  }

  let { curve, formatError, unit }: Props = $props();

  /** The strongest tint, in percent; text on it keeps at least 5:1 in every theme. */
  const MAX_TINT = 55;
  /** A sparse cell's tint is this share of what its bias would give. */
  const SPARSE_TINT = 0.4;

  const hours = $derived(curve.byHour ?? []);
  const columns = $derived(curve.horizons ?? []);

  /** The cell's tint as a CSS colour, or null for none. */
  function tintOf(h: PredictionHorizonAccuracy | undefined): string | null {
    const bias = h?.error?.mean;
    if (bias == null || bias === 0 || h?.biasScale == null) return null;
    const share = h.biasScale * MAX_TINT * (h.error?.sparse ? SPARSE_TINT : 1);
    return `color-mix(in oklab, var(${bias > 0 ? "--bias-high" : "--bias-low"}) ${Math.round(share)}%, transparent)`;
  }

  const cellAt = (hour: number, minute: number | undefined) =>
    hours.find((h) => h.hour === hour)?.horizons?.find((h) => h.minutes === minute);
</script>

<div class="space-y-2">
  <div class="overflow-x-auto">
    <table class="w-full border-separate border-spacing-0.5 text-xs tabular-nums">
      <caption class="sr-only">
        {curveLabel(curve.curve)} bias by hour of day and horizon, in {unit}. A positive value means that the prediction was too high.
      </caption>
      <thead>
        <tr>
          <th scope="col" class="px-2 py-1 text-left align-bottom font-normal text-muted-foreground">Hour</th>
          {#each columns as column (column.minutes)}
            <th
              scope="col"
              class="rounded-sm px-2 py-1 text-right align-bottom font-normal {column.settingsSignal
                ? 'bg-muted text-foreground'
                : 'text-muted-foreground'}"
            >
              {#if column.settingsSignal}<span class="block text-2xs text-muted-foreground">Settings signal</span>{/if}
              {horizonLabel(column.minutes)}
            </th>
          {/each}
        </tr>
      </thead>
      <tbody>
        {#each hours as hour (hour.hour)}
          <tr>
            <th scope="row" class="px-2 py-0.5 text-left font-normal text-muted-foreground">
              {hourLabel(atMinute((hour.hour ?? 0) * 60))}
            </th>
            {#each columns as column (column.minutes)}
              {@const cell = cellAt(hour.hour ?? 0, column.minutes)}
              <td
                class="rounded-sm bg-(--tint) px-2 py-0.5 text-right {cell?.error?.sparse ? 'italic' : ''}"
                style:--tint={tintOf(cell) ?? "transparent"}
              >
                {cell?.error ? formatError(cell.error.mean ?? 0) : "–"}
              </td>
            {/each}
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
  <div class="flex flex-wrap items-center gap-x-4 gap-y-1 px-1 text-xs text-muted-foreground">
    <span class="flex items-center gap-1.5">
      <span class="swatch-low inline-block size-3 rounded-sm"></span>
      Predicted too low
    </span>
    <span class="flex items-center gap-1.5">
      <span class="inline-block size-3 rounded-sm border"></span>
      Near zero
    </span>
    <span class="flex items-center gap-1.5">
      <span class="swatch-high inline-block size-3 rounded-sm"></span>
      Predicted too high
    </span>
    <span><span class="italic">Italic</span> and faded: too few days to show a pattern</span>
  </div>
</div>

<style>
  .swatch-low {
    background-color: color-mix(in oklab, var(--bias-low) 55%, transparent);
  }

  .swatch-high {
    background-color: color-mix(in oklab, var(--bias-high) 55%, transparent);
  }
</style>
