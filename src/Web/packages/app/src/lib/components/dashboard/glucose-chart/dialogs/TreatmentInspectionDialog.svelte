<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Badge } from "$lib/components/ui/badge";
  import { Syringe } from "lucide-svelte";
  import { bg, bgLabel, formatLocale, time } from "$lib/utils/formatting";
  import { getDataSourceDisplayName } from "$lib/utils/data-source-display";
  import type { BolusCalculation } from "$lib/api";
  import { CalculationType } from "$lib/api";
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";
  import { getAll as getBolusCalculations } from "$lib/api/generated/bolusCalculations.generated.remote";
  import { useApsPrediction } from "./aps-prediction.svelte";
  import { inspectionSearchWindow } from "./inspection-window";
  import InspectionChart from "./InspectionChart.svelte";
  import InspectionFooter from "./InspectionFooter.svelte";

  interface Props {
    open: boolean;
    timestamp: Date;
    bolusInsulin?: number;
    bolusType?: string;
    bolusDataSource?: string;
    carbGrams?: number;
    carbLabel?: string;
    carbDataSource?: string;
    entry?: EntryRecord | null;
    correlatedRecords?: EntryRecord[];
    iob?: number;
    cob?: number;
    glucoseValue?: number;
    glucoseData: { time: Date; sgv: number; color: string }[];
    highThreshold: number;
    lowThreshold: number;
    hasGlucoseContext: boolean;
    hasDeliveryContext: boolean;
    onClose: () => void;
    onNavigateGlucose?: () => void;
    onNavigateDelivery?: () => void;
    onEditEntry?: () => void;
  }

  let {
    open = $bindable(),
    timestamp,
    bolusInsulin,
    bolusType,
    bolusDataSource,
    carbGrams,
    carbLabel,
    carbDataSource,
    entry: _entry,
    correlatedRecords,
    iob,
    cob,
    glucoseValue,
    glucoseData,
    highThreshold,
    lowThreshold,
    hasGlucoseContext,
    hasDeliveryContext,
    onClose,
    onNavigateGlucose,
    onNavigateDelivery,
    onEditEntry,
  }: Props = $props();

  const bolusSourceName = $derived(getDataSourceDisplayName(bolusDataSource));
  const carbSourceName = $derived(getDataSourceDisplayName(carbDataSource));
  // Show a single "Source" row if both come from the same source, otherwise per-treatment sources
  const unifiedSourceName = $derived(
    bolusSourceName && carbSourceName && bolusSourceName === carbSourceName
      ? bolusSourceName
      : null,
  );

  let bolusCalc: BolusCalculation | null = $state(null);

  const aps = useApsPrediction(
    () => open,
    () => timestamp,
  );

  // Build the treatment header summary
  const treatmentSummary = $derived.by(() => {
    const parts: string[] = [];
    if (bolusInsulin != null) {
      let bolusText = `${bolusInsulin}U`;
      if (bolusType) bolusText += ` ${bolusType}`;
      else bolusText += " bolus";
      parts.push(bolusText);
    }
    if (carbGrams != null) {
      let carbText = `${carbGrams}g carbs`;
      if (carbLabel) carbText += ` (${carbLabel})`;
      parts.push(carbText);
    }
    return parts.join(" + ");
  });

  // Build the chart center label
  const chartLabel = $derived.by(() => {
    if (bolusInsulin != null && carbGrams != null) {
      return `${bolusInsulin}U + ${carbGrams}g`;
    }
    if (bolusInsulin != null) return `${bolusInsulin}U bolus`;
    if (carbGrams != null) return `${carbGrams}g carbs`;
    return undefined;
  });

  // Calculation type badge styling
  const calcTypeBadgeClass = $derived.by(() => {
    if (!bolusCalc?.calculationType) return "";
    switch (bolusCalc.calculationType) {
      case CalculationType.Suggested:
        return "bg-blue-500/20 text-blue-400 border-blue-500/30";
      case CalculationType.Manual:
        return "bg-orange-500/20 text-orange-400 border-orange-500/30";
      case CalculationType.Automatic:
        return "bg-green-500/20 text-green-400 border-green-500/30";
      default:
        return "bg-muted text-muted-foreground border-border";
    }
  });

  // Format entry summary for correlated records (matches TreatmentDisambiguationDialog)
  function formatEntrySummary(record: EntryRecord): string {
    const parts: string[] = [];
    switch (record.kind) {
      case "bolus":
        if (record.data.insulin) parts.push(`${record.data.insulin}U`);
        if (record.data.bolusType) parts.push(record.data.bolusType);
        break;
      case "carbs":
        if (record.data.carbs) parts.push(`${record.data.carbs}g carbs`);
        break;
      case "bgCheck":
        if (record.data.mgdl) parts.push(`${record.data.mgdl} mg/dL`);
        break;
      case "note":
        if (record.data.text) parts.push(record.data.text.slice(0, 50));
        break;
      case "deviceEvent":
        if (record.data.eventType) parts.push(record.data.eventType);
        break;
    }
    return parts.join(" · ") || ENTRY_CATEGORIES[record.kind].name;
  }

  // Fetch the bolus calculation behind the treatment on open
  $effect(() => {
    if (!open) return;
    bolusCalc = null;
    let cancelled = false;
    const { from, to } = inspectionSearchWindow(timestamp);
    getBolusCalculations({ from, to, limit: 1, sort: "timestamp_desc" })
      .then((result) => {
        if (!cancelled && result?.data?.length) {
          bolusCalc = result.data[0];
        }
      })
      .catch(() => {
        /* Bolus calculation data is optional */
      });
    return () => {
      cancelled = true;
    };
  });
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-lg max-h-[85vh] overflow-y-auto print:hidden">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-3 flex-wrap">
        {#if bolusInsulin != null}
          <Badge
            variant="outline"
            class="{ENTRY_CATEGORIES.bolus.colorClass} {ENTRY_CATEGORIES.bolus.bgClass} {ENTRY_CATEGORIES.bolus.borderClass}"
          >
            <Syringe class="mr-1 h-3.5 w-3.5" />
            {bolusInsulin}U{bolusType ? ` ${bolusType}` : ""}
          </Badge>
        {/if}
        {#if carbGrams != null}
          <Badge
            variant="outline"
            class="{ENTRY_CATEGORIES.carbs.colorClass} {ENTRY_CATEGORIES.carbs.bgClass} {ENTRY_CATEGORIES.carbs.borderClass}"
          >
            {carbGrams}g{carbLabel ? ` ${carbLabel}` : " carbs"}
          </Badge>
        {/if}
      </Dialog.Title>
      <Dialog.Description>
        {time(timestamp, { seconds: true })}
        &mdash;
        {timestamp.toLocaleDateString(formatLocale(), {
          month: "short",
          day: "numeric",
        })}
        {#if treatmentSummary}
          <span class="block text-xs mt-1">{treatmentSummary}</span>
        {/if}
      </Dialog.Description>
    </Dialog.Header>

    <!-- Context row: IOB, COB, glucose, source -->
    {#if iob != null || cob != null || glucoseValue != null || bolusSourceName || carbSourceName}
      <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm py-3">
        {#if glucoseValue != null}
          <span class="text-muted-foreground">BG at time</span>
          <span class="font-medium">{bg(glucoseValue)} {bgLabel()}</span>
        {/if}

        {#if iob != null}
          <span class="text-muted-foreground">IOB</span>
          <span class="font-medium">{iob.toFixed(1)} U</span>
        {/if}

        {#if cob != null && cob > 0}
          <span class="text-muted-foreground">COB</span>
          <span class="font-medium">{cob.toFixed(0)} g</span>
        {/if}

        {#if unifiedSourceName}
          <span class="text-muted-foreground">Source</span>
          <span class="font-medium">{unifiedSourceName}</span>
        {:else}
          {#if bolusSourceName}
            <span class="text-muted-foreground">Bolus Source</span>
            <span class="font-medium">{bolusSourceName}</span>
          {/if}
          {#if carbSourceName}
            <span class="text-muted-foreground">Carb Source</span>
            <span class="font-medium">{carbSourceName}</span>
          {/if}
        {/if}
      </div>
    {/if}

    <!-- Decision audit section (conditional on bolus calculation) -->
    {#if bolusCalc}
      <div class="py-3">
        <p class="text-xs font-medium text-muted-foreground mb-2 uppercase tracking-wide">
          Decision Audit
        </p>
        <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
          {#if bolusCalc.bloodGlucoseInput != null}
            <span class="text-muted-foreground">BG Input</span>
            <span class="font-medium">
              {bg(bolusCalc.bloodGlucoseInput)} {bgLabel()}
              {#if bolusCalc.bloodGlucoseInputSource}
                <span class="text-xs text-muted-foreground">
                  ({bolusCalc.bloodGlucoseInputSource})
                </span>
              {/if}
            </span>
          {/if}

          {#if bolusCalc.carbInput != null}
            <span class="text-muted-foreground">Carb Input</span>
            <span class="font-medium">{bolusCalc.carbInput} g</span>
          {/if}

          {#if bolusCalc.carbRatio != null}
            <span class="text-muted-foreground">Carb Ratio</span>
            <span class="font-medium">1:{bolusCalc.carbRatio}</span>
          {/if}

          {#if bolusCalc.insulinRecommendation != null}
            <span class="text-muted-foreground">Recommended</span>
            <span class="font-medium">{bolusCalc.insulinRecommendation.toFixed(2)} U</span>
          {/if}

          {#if bolusCalc.insulinProgrammed != null}
            <span class="text-muted-foreground">Programmed</span>
            <span class="font-medium">{bolusCalc.insulinProgrammed.toFixed(2)} U</span>
          {/if}

          {#if bolusCalc.insulinOnBoard != null}
            <span class="text-muted-foreground">IOB Correction</span>
            <span class="font-medium">{bolusCalc.insulinOnBoard.toFixed(2)} U</span>
          {/if}

          {#if bolusCalc.calculationType}
            <span class="text-muted-foreground">Calculation Type</span>
            <span>
              <Badge variant="outline" class={calcTypeBadgeClass}>
                {bolusCalc.calculationType}
              </Badge>
            </span>
          {/if}

          {#if bolusCalc.splitNow != null && bolusCalc.splitExt != null}
            <span class="text-muted-foreground">Split Bolus</span>
            <span class="font-medium">{bolusCalc.splitNow}% now / {bolusCalc.splitExt}% ext</span>
          {/if}

          {#if bolusCalc.preBolus != null && bolusCalc.preBolus > 0}
            <span class="text-muted-foreground">Pre-bolus</span>
            <span class="font-medium">{bolusCalc.preBolus} min</span>
          {/if}
        </div>
      </div>
    {/if}

    <InspectionChart
      {glucoseData}
      centerTime={timestamp}
      predictionData={aps.predictionData}
      {highThreshold}
      {lowThreshold}
      beforeMs={15 * 60 * 1000}
      afterMs={3 * 60 * 60 * 1000}
      label={chartLabel}
    />

    <!-- Related entries (conditional) -->
    {#if correlatedRecords && correlatedRecords.length > 0}
      <div class="py-3">
        <p class="text-xs font-medium text-muted-foreground mb-2 uppercase tracking-wide">
          Related Entries
        </p>
        <div class="space-y-2">
          {#each correlatedRecords as record, i (record.data.id ?? `${record.data.mills}-${i}`)}
            {@const category = ENTRY_CATEGORIES[record.kind]}
            <button
              type="button"
              class="w-full flex items-center gap-3 p-3 rounded-lg bg-muted hover:bg-muted/80 transition-colors text-left"
              onclick={() => {
                /* correlated record click — currently informational */
              }}
            >
              <div class="flex-1">
                <div class="font-medium text-sm">
                  {formatEntrySummary(record)}
                </div>
                <div class="text-xs text-muted-foreground">
                  {record.data.mills
                    ? time(record.data.mills)
                    : ""}
                </div>
              </div>
              <Badge variant="outline" class="text-xs {category.colorClass}">
                {category.name}
              </Badge>
            </button>
          {/each}
        </div>
      </div>
    {/if}

    <InspectionFooter
      onNavigateGlucose={hasGlucoseContext ? onNavigateGlucose : undefined}
      onNavigateDelivery={hasDeliveryContext ? onNavigateDelivery : undefined}
      {onEditEntry}
      {onClose}
    />
  </Dialog.Content>
</Dialog.Root>
