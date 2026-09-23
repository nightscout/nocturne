<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Activity, Syringe, Utensils, TriangleAlert } from '@lucide/svelte';
  import { BasalDeliveryOrigin } from '../enums.js';
  import { bg, bgLabel, getDataSourceDisplayName } from '../utils/formatting.js';
  import GlucoseResponseChart from './GlucoseResponseChart.svelte';

  // ---------------------------------------------------------------------------
  // Inlined PredictionData types (no dependency on $api/predictions.remote)
  // ---------------------------------------------------------------------------

  interface PredictionPoint {
    timestamp: number;
    value: number;
  }

  interface PredictionCurves {
    main: PredictionPoint[];
    iobOnly: PredictionPoint[];
    uam: PredictionPoint[];
    cob: PredictionPoint[];
    zeroTemp: PredictionPoint[];
  }

  interface PredictionData {
    timestamp: Date;
    currentBg: number;
    delta: number;
    eventualBg: number;
    iob: number;
    cob: number;
    sensitivityRatio: number | null;
    intervalMinutes: number;
    curves: PredictionCurves;
  }

  // ---------------------------------------------------------------------------
  // Minimal ApsSnapshot type (inlined — not imported from $lib/api)
  // ---------------------------------------------------------------------------

  interface ApsSnapshot {
    enacted?: boolean | null;
    enactedRate?: number | null;
    enactedDuration?: number | null;
    enactedBolusVolume?: number | null;
    currentBg?: number | null;
    eventualBg?: number | null;
    targetBg?: number | null;
    iob?: number | null;
    basalIob?: number | null;
    bolusIob?: number | null;
    cob?: number | null;
    sensitivityRatio?: number | null;
  }

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
    predictionData?: PredictionData | null;
    snapshot?: ApsSnapshot | null;
    onClose: () => void;
    onNavigateGlucose?: () => void;
    onNavigateTreatment?: () => void;
    dialog: Snippet<[{ open: boolean; onOpenChange: (v: boolean) => void; children: Snippet }]>;
    badge: Snippet<[{ variant?: 'default' | 'outline' | 'destructive'; class?: string; children: Snippet }]>;
    button: Snippet<[{ onclick: () => void; variant?: string; size?: string; children: Snippet }]>;
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
    predictionData = null,
    snapshot = null,
    onClose,
    onNavigateGlucose,
    onNavigateTreatment,
    dialog,
    badge,
    button,
  }: Props = $props();

  const sourceDisplayName = $derived(getDataSourceDisplayName(dataSource));

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
        return "bg-status-info/20 text-status-info border-status-info/30";
      case BasalDeliveryOrigin.Suspended:
        return "bg-status-critical/20 text-status-critical border-status-critical/30";
      case BasalDeliveryOrigin.Manual:
        return "bg-status-warning/20 text-status-warning border-status-warning/30";
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
</script>

{@render dialog({ open, onOpenChange: (v) => (open = v), children: dialogContent })}

{#snippet dialogContent()}
  <div class="flex flex-col gap-0">
    <!-- Header -->
    <div class="flex flex-col gap-1.5 p-6 pb-0">
      <div class="flex items-center gap-3">
        <Syringe class="h-5 w-5 text-muted-foreground" />
        {#if basalRate != null}
          <span class="text-3xl font-bold">
            {basalRate.toFixed(2)} U/hr
          </span>
        {:else}
          <span class="text-3xl font-bold text-muted-foreground">--</span>
        {/if}
        {@render badge({ variant: 'outline', class: deliveryBadgeClass, children: deliveryModeContent })}
        {#snippet deliveryModeContent()}
          {deliveryMode}
        {/snippet}
      </div>
      <p class="text-sm text-muted-foreground">
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
      </p>
    </div>

    <!-- Loop decision section (conditional on APS snapshot) -->
    {#if snapshot}
      <div class="py-3 px-6">
        <p class="text-xs font-medium text-muted-foreground mb-2 uppercase tracking-wide">
          Loop Decision
        </p>
        <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
          <span class="text-muted-foreground">Status</span>
          <span>
            {#if snapshot.enacted}
              {@render badge({ variant: 'outline', class: 'bg-status-normal/20 text-status-normal border-status-normal/30', children: enactedContent })}
              {#snippet enactedContent()}
                Enacted
              {/snippet}
            {:else}
              {@render badge({ variant: 'outline', class: 'bg-status-warning/20 text-status-warning border-status-warning/30', children: suggestedContent })}
              {#snippet suggestedContent()}
                Suggested Only
              {/snippet}
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
      <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm py-3 px-6">
        <span class="text-muted-foreground">IOB</span>
        <span class="font-medium">{iob.toFixed(2)} U</span>
      </div>
    {/if}

    <!-- Glucose response chart (shows glucose trace, with prediction overlay when available) -->
    {#if chartGlucoseData.length > 0}
      <div class="py-2 px-6">
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
      <div class="py-3 px-6">
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
      <div class="flex items-center gap-2 py-2 px-6 text-status-warning text-sm">
        <TriangleAlert class="h-4 w-4" />
        <span>Basal data may be stale. The last update was received some time ago.</span>
      </div>
    {/if}

    {#if sourceDisplayName}
      <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm py-2 px-6">
        <span class="text-muted-foreground">Source</span>
        <span class="font-medium">{sourceDisplayName}</span>
      </div>
    {/if}

    <!-- Footer -->
    <div class="flex gap-2 p-6 pt-0">
      {#if hasGlucoseContext && onNavigateGlucose}
        {@render button({ onclick: onNavigateGlucose, variant: 'outline', size: 'sm', children: glucoseBtnContent })}
        {#snippet glucoseBtnContent()}
          <Activity class="mr-1.5 h-4 w-4" />
          Glucose
        {/snippet}
      {/if}
      {#if hasTreatmentContext && onNavigateTreatment}
        {@render button({ onclick: onNavigateTreatment, variant: 'outline', size: 'sm', children: treatmentBtnContent })}
        {#snippet treatmentBtnContent()}
          <Utensils class="mr-1.5 h-4 w-4" />
          Treatments
        {/snippet}
      {/if}
      {@render button({ onclick: onClose, variant: 'secondary', size: 'sm', children: closeBtnContent })}
      {#snippet closeBtnContent()}
        Close
      {/snippet}
    </div>
  </div>
{/snippet}
