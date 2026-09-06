<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Badge } from "$lib/components/ui/badge";
  import { BasalDeliveryOrigin } from "$lib/api";
  import { bg, bgDelta, bgLabel, formatLocale, time } from "$lib/utils/formatting";
  import { getDataSourceDisplayName } from "$lib/utils/data-source-display";
  import { useApsPrediction } from "./aps-prediction.svelte";
  import InspectionChart from "./InspectionChart.svelte";
  import InspectionFooter from "./InspectionFooter.svelte";

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

  const aps = useApsPrediction(
    () => open,
    () => timestamp,
  );

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
        {time(timestamp, { seconds: true })}
        &mdash;
        {timestamp.toLocaleDateString(formatLocale(), {
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

    <InspectionChart
      {glucoseData}
      centerTime={timestamp}
      predictionData={aps.predictionData}
      {highThreshold}
      {lowThreshold}
      beforeMs={15 * 60 * 1000}
      afterMs={3 * 60 * 60 * 1000}
    />

    <InspectionFooter
      onNavigateDelivery={hasDeliveryContext ? onNavigateDelivery : undefined}
      onNavigateTreatment={hasTreatmentContext ? onNavigateTreatment : undefined}
      {onClose}
    />
  </Dialog.Content>
</Dialog.Root>
