<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Activity, Syringe, Utensils, AlertTriangle } from "lucide-svelte";
  import { BasalDeliveryOrigin, type ApsSnapshot } from "$lib/api";
  import { bg, bgLabel } from "$lib/utils/formatting";
  import { getDataSourceDisplayName } from "$lib/utils/data-source-display";
  import type { PredictionData } from "$api/predictions.remote";
  import { getAll as getApsSnapshots } from "$lib/api/generated/apsSnapshots.generated.remote";
  import { apsSnapshotToPrediction } from "$lib/utils/aps-snapshot-to-prediction";
  import GlucoseResponseChart from "./GlucoseResponseChart.svelte";

  interface Props {
    open: boolean;
    timestamp: Date;
    basalRate?: number;
    scheduledBasalRate?: number;
    basalOrigin?: BasalDeliveryOrigin;
    tempBasalRate?: number | null;
    tempBasalPercent?: number | null;
    pumpMode?: string;
    overrideState?: string;
    profileName?: string;
    activityStates?: string[];
    iob?: number;
    isStaleBasal: boolean;
    dataSource?: string;
    glucoseData: { time: Date; sgv: number; color: string }[];
    highThreshold: number;
    lowThreshold: number;
    hasGlucoseContext: boolean;
    hasTreatmentContext: boolean;
    onClose: () => void;
    onNavigateGlucose?: () => void;
    onNavigateTreatment?: () => void;
  }

  let {
    open = $bindable(),
    timestamp,
    basalRate,
    scheduledBasalRate,
    basalOrigin,
    tempBasalRate,
    tempBasalPercent,
    pumpMode,
    overrideState,
    profileName,
    activityStates,
    iob,
    isStaleBasal,
    dataSource,
    glucoseData,
    highThreshold,
    lowThreshold,
    hasGlucoseContext,
    hasTreatmentContext,
    onClose,
    onNavigateGlucose,
    onNavigateTreatment,
  }: Props = $props();

  const sourceDisplayName = $derived(getDataSourceDisplayName(dataSource));

  let snapshot: ApsSnapshot | null = $state(null);
  let predictionData: PredictionData | null = $state(null);

  // Derive delivery mode badge from basalOrigin
  const deliveryMode = $derived.by(() => {
    switch (basalOrigin) {
      case BasalDeliveryOrigin.Algorithm:
        return "Auto";
      case BasalDeliveryOrigin.Manual:
        return "Manual";
      case BasalDeliveryOrigin.Suspended:
        return "Suspended";
      case BasalDeliveryOrigin.Scheduled:
        return "Scheduled";
      case BasalDeliveryOrigin.Inferred:
        return "Inferred";
      default:
        return pumpMode ?? "Unknown";
    }
  });

  const deliveryBadgeClass = $derived.by(() => {
    switch (basalOrigin) {
      case BasalDeliveryOrigin.Algorithm:
        return "bg-blue-500/20 text-blue-400 border-blue-500/30";
      case BasalDeliveryOrigin.Suspended:
        return "bg-red-500/20 text-red-400 border-red-500/30";
      case BasalDeliveryOrigin.Manual:
        return "bg-orange-500/20 text-orange-400 border-orange-500/30";
      default:
        return "bg-muted text-muted-foreground border-border";
    }
  });

  // Format sensitivity ratio as percentage deviation with semantic label
  function formatSensitivity(ratio: number | undefined | null): string | null {
    if (ratio == null) return null;
    const deviation = Math.round((ratio - 1) * 100);
    if (deviation === 0) return "0% (nominal)";
    const label = deviation > 0 ? "more sensitive" : "less sensitive";
    const prefix = deviation > 0 ? `+${deviation}%` : `${deviation}%`;
    return `${prefix} (${label})`;
  }

  // Filter glucose data for the prediction chart window (+-30 min)
  const chartGlucoseData = $derived.by(() => {
    const centerMs = timestamp.getTime();
    const minMs = centerMs - 30 * 60 * 1000;
    const predictionHorizonMs = predictionData?.curves.main.length
      ? Math.max(...predictionData.curves.main.map((p) => p.timestamp))
      : centerMs + 30 * 60 * 1000;
    const maxMs = Math.max(centerMs + 30 * 60 * 1000, predictionHorizonMs);
    return glucoseData.filter((d) => {
      const t = d.time.getTime();
      return t >= minMs && t <= maxMs;
    });
  });

  // Whether we have active modifiers to show
  const hasActiveModifiers = $derived(
    !!overrideState ||
      (!!profileName && profileName !== "Default") ||
      tempBasalRate != null ||
      (activityStates != null && activityStates.length > 0),
  );

  // Fetch nearest APS snapshot on open
  $effect(() => {
    if (!open) return;
    snapshot = null;
    predictionData = null;
    let cancelled = false;
    const from = new Date(timestamp.getTime() - 5 * 60 * 1000);
    const to = new Date(timestamp.getTime() + 5 * 60 * 1000);
    getApsSnapshots({ from, to, limit: 1, sort: "timestamp_desc" })
      .then((result) => {
        if (!cancelled && result?.data?.length) {
          snapshot = result.data[0];
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
        <Syringe class="h-5 w-5 text-muted-foreground" />
        {#if basalRate != null}
          <span class="text-3xl font-bold">
            {basalRate.toFixed(2)} U/hr
          </span>
        {:else}
          <span class="text-3xl font-bold text-muted-foreground">--</span>
        {/if}
        <Badge variant="outline" class={deliveryBadgeClass}>
          {deliveryMode}
        </Badge>
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
        {#if tempBasalRate != null}
          <span class="block text-xs mt-1">
            Temp: {tempBasalRate.toFixed(2)} U/hr{tempBasalPercent != null
              ? ` (${tempBasalPercent}%)`
              : ""}
            {#if scheduledBasalRate != null}
              &middot; Scheduled: {scheduledBasalRate.toFixed(2)} U/hr
            {/if}
          </span>
        {:else if scheduledBasalRate != null && basalOrigin === BasalDeliveryOrigin.Algorithm}
          <span class="block text-xs mt-1">
            Scheduled: {scheduledBasalRate.toFixed(2)} U/hr
          </span>
        {/if}
      </Dialog.Description>
    </Dialog.Header>

    <!-- Loop decision section (conditional on APS snapshot) -->
    {#if snapshot}
      <div class="py-3">
        <p class="text-xs font-medium text-muted-foreground mb-2 uppercase tracking-wide">
          Loop Decision
        </p>
        <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
          <span class="text-muted-foreground">Status</span>
          <span>
            {#if snapshot.enacted}
              <Badge variant="outline" class="bg-green-500/20 text-green-400 border-green-500/30">
                Enacted
              </Badge>
            {:else}
              <Badge variant="outline" class="bg-yellow-500/20 text-yellow-400 border-yellow-500/30">
                Suggested Only
              </Badge>
            {/if}
          </span>

          {#if snapshot.enactedRate != null}
            <span class="text-muted-foreground">Enacted Rate</span>
            <span class="font-medium">
              {snapshot.enactedRate.toFixed(2)} U/hr
              {#if snapshot.enactedDuration != null}
                for {snapshot.enactedDuration} min
              {/if}
            </span>
          {/if}

          {#if snapshot.enactedBolusVolume != null && snapshot.enactedBolusVolume > 0}
            <span class="text-muted-foreground">SMB / Auto-bolus</span>
            <span class="font-medium">{snapshot.enactedBolusVolume.toFixed(2)} U</span>
          {/if}

          {#if snapshot.currentBg != null}
            <span class="text-muted-foreground">Current BG</span>
            <span class="font-medium">{bg(snapshot.currentBg)} {bgLabel()}</span>
          {/if}

          {#if snapshot.eventualBg != null}
            <span class="text-muted-foreground">Eventual BG</span>
            <span class="font-medium">{bg(snapshot.eventualBg)} {bgLabel()}</span>
          {/if}

          {#if snapshot.targetBg != null}
            <span class="text-muted-foreground">Target BG</span>
            <span class="font-medium">{bg(snapshot.targetBg)} {bgLabel()}</span>
          {/if}

          {#if snapshot.iob != null}
            <span class="text-muted-foreground">IOB</span>
            <span class="font-medium">
              {snapshot.iob.toFixed(2)} U
              {#if snapshot.basalIob != null || snapshot.bolusIob != null}
                <span class="text-xs text-muted-foreground">
                  ({#if snapshot.basalIob != null}basal {snapshot.basalIob.toFixed(2)}{/if}{#if snapshot.basalIob != null && snapshot.bolusIob != null}, {/if}{#if snapshot.bolusIob != null}bolus {snapshot.bolusIob.toFixed(2)}{/if})
                </span>
              {/if}
            </span>
          {/if}

          {#if snapshot.cob != null}
            <span class="text-muted-foreground">COB</span>
            <span class="font-medium">{snapshot.cob.toFixed(0)} g</span>
          {/if}

          {#if formatSensitivity(snapshot.sensitivityRatio)}
            <span class="text-muted-foreground">Sensitivity</span>
            <span class="font-medium">{formatSensitivity(snapshot.sensitivityRatio)}</span>
          {/if}
        </div>
      </div>
    {:else if iob != null}
      <!-- Fallback: show IOB from props when no APS snapshot -->
      <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm py-3">
        <span class="text-muted-foreground">IOB</span>
        <span class="font-medium">{iob.toFixed(2)} U</span>
      </div>
    {/if}

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

    <!-- Active modifiers -->
    {#if hasActiveModifiers}
      <div class="py-3">
        <p class="text-xs font-medium text-muted-foreground mb-2 uppercase tracking-wide">
          Active Modifiers
        </p>
        <div class="space-y-1.5 text-sm">
          {#if overrideState}
            <div class="flex items-center gap-2">
              <Activity class="h-3.5 w-3.5 text-muted-foreground" />
              <span>Override: <span class="font-medium">{overrideState}</span></span>
            </div>
          {/if}

          {#if profileName && profileName !== "Default"}
            <div class="flex items-center gap-2">
              <Activity class="h-3.5 w-3.5 text-muted-foreground" />
              <span>Profile: <span class="font-medium">{profileName}</span></span>
            </div>
          {/if}

          {#if tempBasalRate != null}
            <div class="flex items-center gap-2">
              <Syringe class="h-3.5 w-3.5 text-muted-foreground" />
              <span>
                Temp Basal: <span class="font-medium">
                  {tempBasalRate.toFixed(2)} U/hr{tempBasalPercent != null
                    ? ` (${tempBasalPercent}%)`
                    : ""}
                </span>
              </span>
            </div>
          {/if}

          {#if activityStates && activityStates.length > 0}
            {#each activityStates as activity}
              <div class="flex items-center gap-2">
                <Activity class="h-3.5 w-3.5 text-muted-foreground" />
                <span class="font-medium">{activity}</span>
              </div>
            {/each}
          {/if}
        </div>
      </div>
    {/if}

    <!-- Pump status: stale data warning -->
    {#if isStaleBasal}
      <div class="flex items-center gap-2 py-2 text-yellow-500 text-sm">
        <AlertTriangle class="h-4 w-4" />
        <span>Basal data may be stale. The last update was received some time ago.</span>
      </div>
    {/if}

    {#if sourceDisplayName}
      <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm py-2">
        <span class="text-muted-foreground">Source</span>
        <span class="font-medium">{sourceDisplayName}</span>
      </div>
    {/if}

    <Dialog.Footer class="flex gap-2">
      {#if hasGlucoseContext && onNavigateGlucose}
        <Button variant="outline" size="sm" onclick={onNavigateGlucose}>
          <Activity class="mr-1.5 h-4 w-4" />
          Glucose
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
