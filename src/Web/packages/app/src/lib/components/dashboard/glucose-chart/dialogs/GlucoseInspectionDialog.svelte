<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Syringe, Utensils } from "lucide-svelte";
  import { BasalDeliveryOrigin } from "$lib/api";
  import { bg, bgLabel, bgDelta } from "$lib/utils/formatting";
  import { getDataSourceDisplayName } from "$lib/utils/data-source-display";
  import type { PredictionData } from "$api/predictions.remote";
  import { getAll as getApsSnapshots } from "$lib/api/generated/apsSnapshots.generated.remote";
  import { apsSnapshotToPrediction } from "$lib/utils/aps-snapshot-to-prediction";
  import GlucoseResponseChart from "./GlucoseResponseChart.svelte";

  interface Props {
    open: boolean;
    timestamp: Date;
    glucoseValue: number;
    glucoseColor: string;
    direction?: string;
    previousGlucoseValue?: number;
    dataSource?: string;
    glucoseData: { time: Date; sgv: number; color: string }[];
    highThreshold: number;
    lowThreshold: number;
    iob?: number;
    cob?: number;
    basalRate?: number;
    scheduledBasalRate?: number;
    basalOrigin?: BasalDeliveryOrigin;
    pumpMode?: string;
    overrideState?: string;
    profileName?: string;
    activityStates?: string[];
    hasDeliveryContext: boolean;
    hasTreatmentContext: boolean;
    onClose: () => void;
    onNavigateDelivery?: () => void;
    onNavigateTreatment?: () => void;
  }

  let {
    open = $bindable(),
    timestamp,
    glucoseValue,
    glucoseColor,
    direction,
    previousGlucoseValue,
    dataSource,
    glucoseData,
    highThreshold,
    lowThreshold,
    iob,
    cob,
    basalRate,
    scheduledBasalRate,
    basalOrigin,
    pumpMode,
    overrideState,
    profileName,
    activityStates,
    hasDeliveryContext,
    hasTreatmentContext,
    onClose,
    onNavigateDelivery,
    onNavigateTreatment,
  }: Props = $props();

  const sourceDisplayName = $derived(getDataSourceDisplayName(dataSource));

  let predictionData: PredictionData | null = $state(null);

  // Determine range status (at-threshold is "In Range", matching getGlucoseColor)
  const rangeStatus = $derived.by(() => {
    if (glucoseValue > highThreshold) return "High";
    if (glucoseValue < lowThreshold) return "Low";
    return "In Range";
  });

  const rangeBadgeClass = $derived.by(() => {
    if (glucoseValue > highThreshold) return "bg-glucose-high/20 text-glucose-high border-glucose-high/30";
    if (glucoseValue < lowThreshold) return "bg-glucose-very-low/20 text-glucose-very-low border-glucose-very-low/30";
    return "bg-glucose-in-range/20 text-glucose-in-range border-glucose-in-range/30";
  });

  // Delta from previous reading
  const delta = $derived(
    previousGlucoseValue != null ? glucoseValue - previousGlucoseValue : null,
  );

  // Format basal rate with origin context
  const basalDisplay = $derived.by(() => {
    if (basalRate == null) return null;
    let text = `${basalRate.toFixed(2)} U/hr`;
    if (scheduledBasalRate != null && basalOrigin === BasalDeliveryOrigin.Algorithm) {
      text += ` (sched: ${scheduledBasalRate.toFixed(2)})`;
    }
    return text;
  });

  // Filter glucose data for the chart window (-15 min to +3 hours, extended by predictions)
  const chartGlucoseData = $derived.by(() => {
    const centerMs = timestamp.getTime();
    const minMs = centerMs - 15 * 60 * 1000;
    const threeHoursMs = centerMs + 3 * 60 * 60 * 1000;
    // If we have prediction data, extend the window to cover predictions
    const predictionHorizonMs = predictionData?.curves.main.length
      ? Math.max(...predictionData.curves.main.map((p) => p.timestamp))
      : threeHoursMs;
    const maxMs = Math.max(threeHoursMs, predictionHorizonMs);
    return glucoseData.filter((d) => {
      const t = d.time.getTime();
      return t >= minMs && t <= maxMs;
    });
  });

  // Fetch nearest APS snapshot on open
  $effect(() => {
    if (!open) return;
    predictionData = null;
    let cancelled = false;
    const from = new Date(timestamp.getTime() - 5 * 60 * 1000);
    const to = new Date(timestamp.getTime() + 5 * 60 * 1000);
    getApsSnapshots({ from, to, limit: 1, sort: "timestamp_desc" })
      .then((result) => {
        if (!cancelled && result?.data?.length) {
          predictionData = apsSnapshotToPrediction(result.data[0]);
        }
      })
      .catch(() => {
        /* APS data is optional */
      });
    return () => {
      cancelled = true;
    };
  });
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-lg max-h-[85vh] overflow-y-auto print:hidden">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-3">
        <span class="text-3xl font-bold" style="color: {glucoseColor}">
          {bg(glucoseValue)}
        </span>
        <span class="text-sm text-muted-foreground">{bgLabel()}</span>
        <Badge variant="outline" class={rangeBadgeClass}>
          {rangeStatus}
        </Badge>
        {#if direction}
          <span class="text-muted-foreground text-sm">{direction}</span>
        {/if}
      </Dialog.Title>
      <Dialog.Description>
        {timestamp.toLocaleTimeString([], {
          hour: "numeric",
          minute: "2-digit",
          second: "2-digit",
        })}
        &mdash;
        {timestamp.toLocaleDateString([], {
          month: "short",
          day: "numeric",
        })}
      </Dialog.Description>
    </Dialog.Header>

    <!-- Context section -->
    <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm py-3">
      {#if delta != null}
        <span class="text-muted-foreground">Delta</span>
        <span class="font-medium">{bgDelta(delta)} {bgLabel()}</span>
      {/if}

      {#if iob != null}
        <span class="text-muted-foreground">IOB</span>
        <span class="font-medium">{iob.toFixed(1)} U</span>
      {/if}

      {#if cob != null && cob > 0}
        <span class="text-muted-foreground">COB</span>
        <span class="font-medium">{cob.toFixed(0)} g</span>
      {/if}

      {#if basalDisplay}
        <span class="text-muted-foreground">Basal</span>
        <span class="font-medium">{basalDisplay}</span>
      {/if}

      {#if pumpMode}
        <span class="text-muted-foreground">Pump Mode</span>
        <span class="font-medium">{pumpMode}</span>
      {/if}

      {#if overrideState}
        <span class="text-muted-foreground">Override</span>
        <span class="font-medium">{overrideState}</span>
      {/if}

      {#if profileName}
        <span class="text-muted-foreground">Profile</span>
        <span class="font-medium">{profileName}</span>
      {/if}

      {#if activityStates && activityStates.length > 0}
        <span class="text-muted-foreground">Activities</span>
        <span class="font-medium">{activityStates.join(", ")}</span>
      {/if}

      {#if sourceDisplayName}
        <span class="text-muted-foreground">Source</span>
        <span class="font-medium">{sourceDisplayName}</span>
      {/if}
    </div>

    <!-- Glucose response chart (shows glucose trace, with prediction overlay when available) -->
    {#if chartGlucoseData.length > 0}
      <div class="py-2">
        <p class="text-xs text-muted-foreground mb-1">Glucose Response</p>
        <GlucoseResponseChart
          glucoseData={chartGlucoseData}
          centerTime={timestamp}
          {predictionData}
          {highThreshold}
          {lowThreshold}
        />
      </div>
    {/if}

    <Dialog.Footer class="flex gap-2">
      {#if hasDeliveryContext && onNavigateDelivery}
        <Button variant="outline" size="sm" onclick={onNavigateDelivery}>
          <Syringe class="mr-1.5 h-4 w-4" />
          Delivery
        </Button>
      {/if}
      {#if hasTreatmentContext && onNavigateTreatment}
        <Button variant="outline" size="sm" onclick={onNavigateTreatment}>
          <Utensils class="mr-1.5 h-4 w-4" />
          Treatments
        </Button>
      {/if}
      <Button variant="secondary" size="sm" onclick={onClose}>
        Close
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
