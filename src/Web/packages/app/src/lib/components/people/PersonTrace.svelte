<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import GlucoseSparkline from "#lib/components/calendar/GlucoseSparkline.svelte";
  import { tracePoints, traceThresholds } from "./person-trace.js";

  interface Props {
    person: TenantOverviewItem;
    class?: string;
  }

  const { person, class: className = "" }: Props = $props();

  const WINDOW_MS = 3 * 60 * 60 * 1000;

  const entries = $derived(tracePoints(person.recentReadings));
  const thresholds = $derived(traceThresholds(person.thresholds, entries));
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
