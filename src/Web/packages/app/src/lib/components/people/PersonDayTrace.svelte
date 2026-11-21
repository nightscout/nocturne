<script lang="ts">
  import type { TenantPersonDetail } from "#lib/api/generated/nocturne-api-client.js";
  import GlucoseSparkline from "#lib/components/calendar/GlucoseSparkline.svelte";
  import { tracePoints, traceThresholds } from "./person-trace.js";

  interface Props {
    detail: TenantPersonDetail;
    class?: string;
  }

  const { detail, class: className = "" }: Props = $props();

  const DAY_MS = 24 * 60 * 60 * 1000;

  const points = $derived(tracePoints(detail.readings));
  const thresholds = $derived(traceThresholds(detail.person?.thresholds, points));
  const day = $derived.by(() => {
    const endMills = detail.to ? new Date(detail.to).getTime() : Date.now();
    const startMills = detail.from
      ? new Date(detail.from).getTime()
      : endMills - DAY_MS;
    return { startMills, endMills };
  });
</script>

{#if points.length > 0}
  <div
    role="img"
    aria-label="Glucose over the last 24 hours"
    class={className}
    data-testid="person-day-trace"
  >
    <GlucoseSparkline entries={points} {day} {thresholds} detailed />
  </div>
{:else}
  <p class="text-sm text-muted-foreground">No readings in the last 24 hours.</p>
{/if}
