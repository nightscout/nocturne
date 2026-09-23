<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import type { EntryCategoryId } from "$lib/constants/entry-categories";
  import type { TreatmentSummary } from "$lib/api";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";
  import { Activity } from "lucide-svelte";
  import { BolusIcon, CarbsIcon } from "$lib/components/icons";

  interface Props {
    treatmentSummary: TreatmentSummary;
    counts: Record<EntryCategoryId | "all", number>;
    /** Calendar days the selected range covers, counting both end days. */
    dayCount: number;
  }

  let { treatmentSummary, counts, dayCount }: Props = $props();

  const totalInsulin = $derived(
    (treatmentSummary.totals?.insulin?.bolus ?? 0) +
      (treatmentSummary.totals?.insulin?.basal ?? 0)
  );
  const totalCarbs = $derived(treatmentSummary.totals?.food?.carbs ?? 0);
  const bolusCount = $derived(counts.bolus);
  const carbEntriesCount = $derived(counts.carbs);

  let dailyAvgCarbs = $derived(totalCarbs / dayCount);
  let dailyAvgBoluses = $derived(bolusCount / dayCount);

  let avgInsulinPerBolus = $derived(
    bolusCount > 0 ? totalInsulin / bolusCount : 0
  );
  let avgCarbsPerEntry = $derived(
    carbEntriesCount > 0 ? totalCarbs / carbEntriesCount : 0
  );
</script>

<div class="@container">
<div class="grid grid-cols-1 @4xl:grid-cols-3 print:grid-cols-3 gap-4">
  <!-- Total Records -->
  <Card.Root>
    <Card.Content class="p-4">
      <div class="flex items-center justify-between">
        <div>
          <p class="text-sm font-medium text-muted-foreground">
            Total Records
          </p>
          <p class="text-2xl font-bold tabular-nums">{counts.all}</p>
        </div>
        <div
          class="h-10 w-10 rounded-lg bg-primary/10 flex items-center justify-center print:hidden"
        >
          <Activity class="h-5 w-5 text-primary" />
        </div>
      </div>
      <div class="mt-2 flex flex-wrap gap-1 print:gap-x-3">
        {#each Object.values(ENTRY_CATEGORIES) as cat (cat.id)}
          {#if counts[cat.id] > 0}
            <Badge variant="secondary" size="sm">
              {cat.name} <span class="opacity-70">{counts[cat.id]}</span>
            </Badge>
          {/if}
        {/each}
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Insulin -->
  <Card.Root>
    <Card.Content class="p-4">
      <div class="flex items-center justify-between">
        <div>
          <p class="text-sm font-medium text-muted-foreground">Insulin</p>
          <p
            class="text-2xl font-bold tabular-nums text-insulin-bolus print:text-foreground"
          >
            {totalInsulin.toFixed(1)}U
          </p>
        </div>
        <div
          class="h-10 w-10 rounded-lg bg-insulin-bolus/10 flex items-center justify-center print:hidden"
        >
          <BolusIcon size={20} />
        </div>
      </div>
      <div
        class="mt-2 flex flex-wrap gap-x-3 gap-y-1 text-xs text-muted-foreground"
      >
        <span>{bolusCount} boluses</span>
        <span>•</span>
        <span>{dailyAvgBoluses.toFixed(1)}/day</span>
        <span>•</span>
        <span>{avgInsulinPerBolus.toFixed(1)}U avg</span>
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Carbs -->
  <Card.Root>
    <Card.Content class="p-4">
      <div class="flex items-center justify-between">
        <div>
          <p class="text-sm font-medium text-muted-foreground">Carbs</p>
          <p class="text-2xl font-bold tabular-nums text-carbs print:text-foreground">
            {totalCarbs.toFixed(0)}g
          </p>
        </div>
        <div
          class="h-10 w-10 rounded-lg bg-carbs/10 flex items-center justify-center print:hidden"
        >
          <CarbsIcon size={20} />
        </div>
      </div>
      <div
        class="mt-2 flex flex-wrap gap-x-3 gap-y-1 text-xs text-muted-foreground"
      >
        <span>{carbEntriesCount} meals</span>
        <span>•</span>
        <span>{dailyAvgCarbs.toFixed(0)}g/day</span>
        <span>•</span>
        <span>{avgCarbsPerEntry.toFixed(0)}g avg/meal</span>
      </div>
    </Card.Content>
  </Card.Root>
</div>
</div>
