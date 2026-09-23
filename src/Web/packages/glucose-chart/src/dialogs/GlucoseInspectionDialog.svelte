<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Syringe, Utensils } from '@lucide/svelte';
  import { BasalDeliveryOrigin } from '../enums.js';
  import { bg, bgLabel, bgDelta, getDataSourceDisplayName } from '../utils/formatting.js';
  import GlucoseResponseChart from './GlucoseResponseChart.svelte';

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
    predictionData?: PredictionData | null;
    onClose: () => void;
    onNavigateDelivery?: () => void;
    onNavigateTreatment?: () => void;
    dialog: Snippet<[{ open: boolean; onOpenChange: (v: boolean) => void; children: Snippet }]>;
    badge: Snippet<[{ variant?: 'default' | 'outline' | 'destructive'; class?: string; children: Snippet }]>;
    button: Snippet<[{ onclick: () => void; variant?: string; size?: string; children: Snippet }]>;
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
    predictionData = null,
    onClose,
    onNavigateDelivery,
    onNavigateTreatment,
    dialog,
    badge,
    button,
  }: Props = $props();

  const sourceDisplayName = $derived(getDataSourceDisplayName(dataSource));

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
</script>

{@render dialog({ open, onOpenChange: (v) => (open = v), children: dialogContent })}

{#snippet dialogContent()}
  <div class="flex flex-col gap-0">
    <!-- Header -->
    <div class="flex flex-col gap-1.5 p-6 pb-0">
      <div class="flex items-center gap-3">
        <span class="text-3xl font-bold text-(--glucose-color)" style:--glucose-color={glucoseColor}>
          {bg(glucoseValue)}
        </span>
        <span class="text-sm text-muted-foreground">{bgLabel()}</span>
        {@render badge({ variant: 'outline', class: rangeBadgeClass, children: rangeStatusContent })}
        {#snippet rangeStatusContent()}
          {rangeStatus}
        {/snippet}
        {#if direction}
          <span class="text-muted-foreground text-sm">{direction}</span>
        {/if}
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
      </p>
    </div>

    <!-- Context section -->
    <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm py-3 px-6">
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

    <!-- Footer -->
    <div class="flex gap-2 p-6 pt-0">
      {#if hasDeliveryContext && onNavigateDelivery}
        {@render button({ onclick: onNavigateDelivery, variant: 'outline', size: 'sm', children: deliveryBtnContent })}
        {#snippet deliveryBtnContent()}
          <Syringe class="mr-1.5 h-4 w-4" />
          Delivery
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
