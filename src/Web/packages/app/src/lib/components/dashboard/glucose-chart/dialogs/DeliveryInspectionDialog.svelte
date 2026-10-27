<script lang="ts">
  import { DEFAULT_PROFILE_NAME } from "#lib/constants/profile-names.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import Activity from "@lucide/svelte/icons/activity";
  import Syringe from "@lucide/svelte/icons/syringe";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import { BasalDeliveryOrigin } from "#lib/api/index.js";
  import { bg, bgLabel, formatLocale, time } from "#lib/utils/formatting.js";
  import { getDataSourceDisplayName } from "#lib/utils/data-source-display.js";
  import { useApsPrediction } from "./aps-prediction.svelte";
  import InspectionChart from "./InspectionChart.svelte";
  import type { GlucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import InspectionFooter from "./InspectionFooter.svelte";

  interface Props {
    glucoseUnits?: GlucoseUnits;
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
    glucoseUnits,
  }: Props = $props();

  const sourceDisplayName = $derived(getDataSourceDisplayName(dataSource));

  const aps = useApsPrediction(
    () => open,
    () => timestamp,
  );
  const snapshot = $derived(aps.snapshot);

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

  const deliveryBadgeVariant = $derived.by(() => {
    switch (basalOrigin) {
      case BasalDeliveryOrigin.Algorithm:
        return "info" as const;
      case BasalDeliveryOrigin.Suspended:
        return "destructive" as const;
      case BasalDeliveryOrigin.Manual:
        return "warning" as const;
      default:
        return "secondary" as const;
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

  // Whether we have active modifiers to show
  const hasActiveModifiers = $derived(
    !!overrideState ||
      (!!profileName && profileName !== DEFAULT_PROFILE_NAME) ||
      tempBasalRate != null ||
      (activityStates != null && activityStates.length > 0),
  );
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
        <Badge variant={deliveryBadgeVariant}>
          {deliveryMode}
        </Badge>
      </Dialog.Title>
      <Dialog.Description>
        {time(timestamp, { seconds: true })}
        &mdash;
        {timestamp.toLocaleDateString(formatLocale(), {
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
          Loop decision
        </p>
        <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
          <span class="text-muted-foreground">Status</span>
          <span>
            {#if snapshot.enacted}
              <Badge variant="success">
                Enacted
              </Badge>
            {:else}
              <Badge variant="warning">
                Suggested only
              </Badge>
            {/if}
          </span>

          {#if snapshot.enactedRate != null}
            <span class="text-muted-foreground">Enacted rate</span>
            <span class="font-medium">
              {snapshot.enactedRate.toFixed(2)} U/hr
              {#if snapshot.enactedDuration != null}
                for {snapshot.enactedDuration} min
              {/if}
            </span>
          {/if}

          {#if snapshot.enactedBolusVolume != null && snapshot.enactedBolusVolume > 0}
            <span class="text-muted-foreground">SMB / auto-bolus</span>
            <span class="font-medium">{snapshot.enactedBolusVolume.toFixed(2)} U</span>
          {/if}

          {#if snapshot.currentBg != null}
            <span class="text-muted-foreground">Current BG</span>
            <span class="font-medium">{bg(snapshot.currentBg, glucoseUnits)} {bgLabel(glucoseUnits)}</span>
          {/if}

          {#if snapshot.eventualBg != null}
            <span class="text-muted-foreground">Eventual BG</span>
            <span class="font-medium">{bg(snapshot.eventualBg, glucoseUnits)} {bgLabel(glucoseUnits)}</span>
          {/if}

          {#if snapshot.targetBg != null}
            <span class="text-muted-foreground">Target BG</span>
            <span class="font-medium">{bg(snapshot.targetBg, glucoseUnits)} {bgLabel(glucoseUnits)}</span>
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

    <InspectionChart
      {glucoseUnits}
      {glucoseData}
      centerTime={timestamp}
      predictionData={aps.predictionData}
      {highThreshold}
      {lowThreshold}
      beforeMs={30 * 60 * 1000}
      afterMs={30 * 60 * 1000}
    />

    <!-- Active modifiers -->
    {#if hasActiveModifiers}
      <div class="py-3">
        <p class="text-xs font-medium text-muted-foreground mb-2 uppercase tracking-wide">
          Active modifiers
        </p>
        <div class="space-y-1.5 text-sm">
          {#if overrideState}
            <div class="flex items-center gap-2">
              <Activity class="h-3.5 w-3.5 text-muted-foreground" />
              <span>Override: <span class="font-medium">{overrideState}</span></span>
            </div>
          {/if}

          {#if profileName && profileName !== DEFAULT_PROFILE_NAME}
            <div class="flex items-center gap-2">
              <Activity class="h-3.5 w-3.5 text-muted-foreground" />
              <span>Profile: <span class="font-medium">{profileName}</span></span>
            </div>
          {/if}

          {#if tempBasalRate != null}
            <div class="flex items-center gap-2">
              <Syringe class="h-3.5 w-3.5 text-muted-foreground" />
              <span>
                Temp basal: <span class="font-medium">
                  {tempBasalRate.toFixed(2)} U/hr{tempBasalPercent != null
                    ? ` (${tempBasalPercent}%)`
                    : ""}
                </span>
              </span>
            </div>
          {/if}

          {#if activityStates && activityStates.length > 0}
            {#each activityStates as activity, i (i)}
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
      <div class="flex items-center gap-2 py-2 text-warning text-sm">
        <AlertTriangle class="h-4 w-4" />
        <span>Basal data is possibly out of date. The last update arrived some time ago.</span>
      </div>
    {/if}

    {#if sourceDisplayName}
      <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm py-2">
        <span class="text-muted-foreground">Source</span>
        <span class="font-medium">{sourceDisplayName}</span>
      </div>
    {/if}

    <InspectionFooter
      onNavigateGlucose={hasGlucoseContext ? onNavigateGlucose : undefined}
      onNavigateTreatment={hasTreatmentContext ? onNavigateTreatment : undefined}
      {onClose}
    />
  </Dialog.Content>
</Dialog.Root>
