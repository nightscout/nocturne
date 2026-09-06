<script lang="ts">
  import GlucoseValueIndicator from "@nocturne/ui/ui/glucose-value-indicator.svelte";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
    trendAngle,
    deltaColorClass,
    getUnitLabel,
    type GlucoseUnits,
  } from "@nocturne/ui/glucose";
  import { ArrowRight } from "@lucide/svelte";
  import type { Reading } from "$lib/glucose-types";

  let {
    reading,
    units,
    now,
  }: { reading: Reading; units: GlucoseUnits; now: number } = $props();

  // Match the web app's staleness threshold so both surfaces agree on "old".
  const STALE_THRESHOLD_MS = 10 * 60 * 1000;

  const isStale = $derived(now - reading.mills > STALE_THRESHOLD_MS);
  const displayBG = $derived(formatGlucoseValue(reading.sgvMgdl, units));
  const delta = $derived(reading.deltaMgdl ?? 0);
  const direction = $derived(reading.direction ?? "Flat");
  const displayDelta = $derived(formatGlucoseDelta(delta, units));
  const angle = $derived(trendAngle(delta));

  const minsAgo = $derived(Math.floor((now - reading.mills) / 60000));
  const timeAgo = $derived(
    minsAgo < 1 ? "just now" : minsAgo === 1 ? "1 min ago" : `${minsAgo} min ago`,
  );
</script>

<div class="flex flex-col items-center gap-2 py-4">
  <div class="flex items-center gap-3">
    <GlucoseValueIndicator
      displayValue={displayBG}
      rawBgMgdl={reading.sgvMgdl}
      {isStale}
      size="lg"
    />
    {#if !isStale}
      <div class="flex items-center gap-1 {deltaColorClass(direction)}">
        <ArrowRight class="size-6" style="transform: rotate({angle}deg)" />
        <span class="text-xl font-medium">{displayDelta}</span>
      </div>
    {/if}
  </div>
  <span class="text-muted-foreground text-xs">{timeAgo} · {getUnitLabel(units)}</span>
</div>
