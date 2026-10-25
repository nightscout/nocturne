<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import Activity from "@lucide/svelte/icons/activity";
  import TimeZoneNote from "$lib/components/reports/TimeZoneNote.svelte";
  import type { AidSystemMetrics } from "$lib/api";
  import { bg, bgLabel } from "$lib/utils/formatting";

  let { metrics }: { metrics: AidSystemMetrics | undefined } = $props();
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <Activity class="w-5 h-5" />
      AID system use
    </CardTitle>
    <CardDescription>
      Automated insulin delivery system metrics
    </CardDescription>
  </CardHeader>
  <CardContent>
    {#if metrics?.timeZoneUnavailableReason}
      <p class="mb-4 text-sm text-muted-foreground">
        <TimeZoneNote reason={metrics.timeZoneUnavailableReason}>
          This report counts device use on UTC days.
        </TimeZoneNote>
      </p>
    {/if}
    <div class="grid grid-cols-1 @xs:grid-cols-2 print:grid-cols-3 gap-4 text-sm">
      <div>
        <div class="text-muted-foreground">CGM</div>
        <div class="font-semibold text-lg">{metrics?.cgmDeviceNames ?? '--'}</div>
      </div>
      <div>
        <div class="text-muted-foreground">Pump</div>
        <div class="font-semibold text-lg">{metrics?.pumpDeviceNames ?? '--'}</div>
      </div>
      <div>
        <div class="text-muted-foreground">CGM active</div>
        <div class="font-semibold text-lg">{metrics?.cgmActivePercent != null ? `${Math.round(metrics.cgmActivePercent)}%` : '--'}</div>
      </div>
      <div>
        <div class="text-muted-foreground">AID active</div>
        <div class="font-semibold text-lg">{metrics?.aidActivePercent != null ? `${Math.round(metrics.aidActivePercent)}%` : '--'}</div>
      </div>
      <div>
        <div class="text-muted-foreground">Target</div>
        <div class="font-semibold text-lg">{metrics?.targetLow != null && metrics?.targetHigh != null ? `${bg(metrics.targetLow)}-${bg(metrics.targetHigh)} ${bgLabel()}` : '--'}</div>
      </div>
      <div>
        <div class="text-muted-foreground">Site changes</div>
        <div class="font-semibold text-lg">{metrics?.siteChangeCount != null ? metrics.siteChangeCount : '--'}</div>
      </div>
    </div>
  </CardContent>
</Card>
