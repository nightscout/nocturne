<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Activity, Syringe } from '@lucide/svelte';
  import { bg, bgLabel, getDataSourceDisplayName } from '../utils/formatting.js';
  import { CalculationType2 } from '../enums.js';
  import GlucoseResponseChart from './GlucoseResponseChart.svelte';

  // ---------------------------------------------------------------------------
  // Inlined from $lib/constants/entry-categories
  // ---------------------------------------------------------------------------

  const ENTRY_CATEGORIES = {
    bolus: {
      id: "bolus" as const,
      name: "Insulin",
      description: "Bolus insulin deliveries",
      icon: "syringe" as const,
      colorClass: "text-entry-bolus",
      badgeClass: "bg-entry-bolus/10 text-entry-bolus border-entry-bolus/30",
    },
    carbs: {
      id: "carbs" as const,
      name: "Carbs",
      description: "Carbohydrate intake records",
      icon: "utensils" as const,
      colorClass: "text-entry-carbs",
      badgeClass: "bg-entry-carbs/10 text-entry-carbs border-entry-carbs/30",
    },
    bgCheck: {
      id: "bgCheck" as const,
      name: "BG Checks",
      description: "Blood glucose measurements",
      icon: "droplet" as const,
      colorClass: "text-entry-bg-check",
      badgeClass: "bg-entry-bg-check/10 text-entry-bg-check border-entry-bg-check/30",
    },
    note: {
      id: "note" as const,
      name: "Notes",
      description: "User annotations and announcements",
      icon: "file-text" as const,
      colorClass: "text-muted-foreground",
      badgeClass: "bg-muted text-muted-foreground border-border",
    },
    deviceEvent: {
      id: "deviceEvent" as const,
      name: "Device Events",
      description: "Sensor, pump, and site changes",
      icon: "smartphone" as const,
      colorClass: "text-entry-device-event",
      badgeClass: "bg-entry-device-event/10 text-entry-device-event border-entry-device-event/30",
    },
  } as const;

  type EntryRecord =
    | { kind: "bolus"; data: { id?: string | null; mills?: number | null; insulin?: number | null; bolusType?: string | null } }
    | { kind: "carbs"; data: { id?: string | null; mills?: number | null; carbs?: number | null } }
    | { kind: "bgCheck"; data: { id?: string | null; mills?: number | null; mgdl?: number | null } }
    | { kind: "note"; data: { id?: string | null; mills?: number | null; text?: string | null } }
    | { kind: "deviceEvent"; data: { id?: string | null; mills?: number | null; eventType?: string | null } };

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
  // Minimal BolusCalculation type (inlined — not imported from $lib/api)
  // ---------------------------------------------------------------------------

  interface BolusCalculation {
    calculationType?: string | null;
    bloodGlucoseInput?: number | null;
    bloodGlucoseInputSource?: string | null;
    carbInput?: number | null;
    carbRatio?: number | null;
    insulinRecommendation?: number | null;
    insulinProgrammed?: number | null;
    insulinOnBoard?: number | null;
    splitNow?: number | null;
    splitExt?: number | null;
    preBolus?: number | null;
  }

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
    predictionData?: PredictionData | null;
    bolusCalc?: BolusCalculation | null;
    onClose: () => void;
    onNavigateGlucose?: () => void;
    onNavigateDelivery?: () => void;
    dialog: Snippet<[{ open: boolean; onOpenChange: (v: boolean) => void; children: Snippet }]>;
    badge: Snippet<[{ variant?: 'default' | 'outline' | 'destructive'; class?: string; children: Snippet }]>;
    button: Snippet<[{ onclick: () => void; variant?: string; size?: string; children: Snippet }]>;
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
    predictionData = null,
    bolusCalc = null,
    onClose,
    onNavigateGlucose,
    onNavigateDelivery,
    dialog,
    badge,
    button,
  }: Props = $props();

  const bolusSourceName = $derived(getDataSourceDisplayName(bolusDataSource));
  const carbSourceName = $derived(getDataSourceDisplayName(carbDataSource ?? undefined));
  // Show a single "Source" row if both come from the same source, otherwise per-treatment sources
  const unifiedSourceName = $derived(
    bolusSourceName && carbSourceName && bolusSourceName === carbSourceName
      ? bolusSourceName
      : null,
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
      case CalculationType2.Suggested:
        return "bg-status-info/20 text-status-info border-status-info/30";
      case CalculationType2.Manual:
        return "bg-status-warning/20 text-status-warning border-status-warning/30";
      case CalculationType2.Automatic:
        return "bg-status-normal/20 text-status-normal border-status-normal/30";
      default:
        return "bg-muted text-muted-foreground border-border";
    }
  });

  // Filter glucose data for the response chart window (-15 min to +3 hours)
  const chartGlucoseData = $derived.by(() => {
    const centerMs = timestamp.getTime();
    const minMs = centerMs - 15 * 60 * 1000;
    const predictionHorizonMs = predictionData?.curves.main.length
      ? Math.max(...predictionData.curves.main.map((p) => p.timestamp))
      : centerMs + 3 * 60 * 60 * 1000;
    const maxMs = Math.max(centerMs + 3 * 60 * 60 * 1000, predictionHorizonMs);
    return glucoseData.filter((d) => {
      const t = d.time.getTime();
      return t >= minMs && t <= maxMs;
    });
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
</script>

{@render dialog({ open, onOpenChange: (v) => (open = v), children: dialogContent })}

{#snippet dialogContent()}
  <div class="flex flex-col gap-0">
    <!-- Header -->
    <div class="flex flex-col gap-1.5 p-6 pb-0">
      <div class="flex items-center gap-3 flex-wrap">
        {#if bolusInsulin != null}
          {@render badge({ variant: 'outline', class: ENTRY_CATEGORIES.bolus.badgeClass, children: bolusBadgeContent })}
          {#snippet bolusBadgeContent()}
            <Syringe class="mr-1 h-3.5 w-3.5" />
            {bolusInsulin}U{bolusType ? ` ${bolusType}` : ""}
          {/snippet}
        {/if}
        {#if carbGrams != null}
          {@render badge({ variant: 'outline', class: ENTRY_CATEGORIES.carbs.badgeClass, children: carbBadgeContent })}
          {#snippet carbBadgeContent()}
            {carbGrams}g{carbLabel ? ` ${carbLabel}` : " carbs"}
          {/snippet}
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
        {#if treatmentSummary}
          <span class="block text-xs mt-1">{treatmentSummary}</span>
        {/if}
      </p>
    </div>

    <!-- Context row: IOB, COB, glucose, source -->
    {#if iob != null || cob != null || glucoseValue != null || bolusSourceName || carbSourceName}
      <div class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm py-3 px-6">
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
      <div class="py-3 px-6">
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
              {@render badge({ variant: 'outline', class: calcTypeBadgeClass, children: calcTypeBadgeContent })}
              {#snippet calcTypeBadgeContent()}
                {bolusCalc.calculationType}
              {/snippet}
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

    <!-- Glucose response chart -->
    {#if chartGlucoseData.length > 0}
      <div class="py-2 px-6">
        <p class="text-xs text-muted-foreground mb-1">Glucose Response</p>
        <GlucoseResponseChart
          glucoseData={chartGlucoseData}
          centerTime={timestamp}
          {predictionData}
          {highThreshold}
          {lowThreshold}
          label={chartLabel}
        />
      </div>
    {/if}

    <!-- Related entries (conditional) -->
    {#if correlatedRecords && correlatedRecords.length > 0}
      <div class="py-3 px-6">
        <p class="text-xs font-medium text-muted-foreground mb-2 uppercase tracking-wide">
          Related Entries
        </p>
        <div class="space-y-2">
          {#each correlatedRecords as record, i (record.data.id ?? `${record.data.mills}-${i}`)}
            {@const category = ENTRY_CATEGORIES[record.kind]}
            <div class="w-full flex items-center gap-3 p-3 rounded-lg bg-muted text-left">
              <div class="flex-1">
                <div class="font-medium text-sm">
                  {formatEntrySummary(record)}
                </div>
                <div class="text-xs text-muted-foreground">
                  {record.data.mills
                    ? new Date(record.data.mills).toLocaleTimeString([], {
                        hour: "numeric",
                        minute: "2-digit",
                      })
                    : ""}
                </div>
              </div>
              {@render badge({ variant: 'outline', class: `text-xs ${category.colorClass}`, children: recordBadgeContent })}
              {#snippet recordBadgeContent()}
                {category.name}
              {/snippet}
            </div>
          {/each}
        </div>
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
      {#if hasDeliveryContext && onNavigateDelivery}
        {@render button({ onclick: onNavigateDelivery, variant: 'outline', size: 'sm', children: deliveryBtnContent })}
        {#snippet deliveryBtnContent()}
          <Syringe class="mr-1.5 h-4 w-4" />
          Delivery
        {/snippet}
      {/if}
      {@render button({ onclick: onClose, variant: 'secondary', size: 'sm', children: closeBtnContent })}
      {#snippet closeBtnContent()}
        Close
      {/snippet}
    </div>
  </div>
{/snippet}
