<script lang="ts">
  import type { TenantOverviewItem } from "#lib/api/generated/nocturne-api-client.js";
  import GlucoseSparkline from "#lib/components/calendar/GlucoseSparkline.svelte";
  import { FALLBACK_GLUCOSE_Y_MAX } from "@nocturne/charts/core";

  interface Props {
    person: TenantOverviewItem;
    class?: string;
  }

  const { person, class: className = "" }: Props = $props();

  const WINDOW_MS = 3 * 60 * 60 * 1000;
  const Y_HEADROOM = 1.05;

  const entries = $derived(
    (person.recentReadings ?? []).flatMap((point) =>
      point.timestamp && point.mgdl != null
        ? [{ mills: new Date(point.timestamp).getTime(), mgdl: point.mgdl }]
        : []
    )
  );
  /** A reading above the shared ceiling would be clipped flat, so the range grows to hold the highest one. */
  const glucoseYMax = $derived(
    Math.max(
      FALLBACK_GLUCOSE_Y_MAX,
      person.thresholds?.urgentHigh ?? 0,
      ...entries.map((entry) => entry.mgdl * Y_HEADROOM)
    )
  );
  const thresholds = $derived({
    veryLow: person.thresholds?.urgentLow,
    low: person.thresholds?.low,
    high: person.thresholds?.high,
    veryHigh: person.thresholds?.urgentHigh,
    glucoseYMax,
  });
  const traceWindow = $derived.by(() => {
    const endMills = Math.max(...entries.map((entry) => entry.mills), Date.now());
    return { startMills: endMills - WINDOW_MS, endMills };
  });
</script>

{#if entries.length > 0}
  <div
    role="img"
    aria-label="Glucose over the last 3 hours"
    class={className}
    data-testid="person-trace"
  >
    <GlucoseSparkline {entries} day={traceWindow} {thresholds} />
  </div>
{/if}
