<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import type { TreatmentSummary } from "$lib/api";
  import type { EntryCategoryId } from "$lib/constants/entry-categories";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";
  import { Activity } from "lucide-svelte";
  import { BolusIcon, CarbsIcon } from "$lib/components/icons";

  interface Props {
    treatmentSummary: TreatmentSummary;
    counts: Record<EntryCategoryId | "all", number>;
    dateRange: { from: string; to: string };
  }

  let { treatmentSummary, counts, dateRange }: Props = $props();

  let daysInRange = $derived.by(() => {
    const from = new Date(dateRange.from);
    const to = new Date(dateRange.to);
    return Math.max(
      1,
      Math.ceil((to.getTime() - from.getTime()) / (1000 * 60 * 60 * 24))
    );
  });

  const totalInsulin = $derived(
    (treatmentSummary.totals?.insulin?.bolus ?? 0) +
      (treatmentSummary.totals?.insulin?.basal ?? 0)
  );
  const totalCarbs = $derived(treatmentSummary.totals?.food?.carbs ?? 0);
  const bolusCount = $derived(counts.bolus);
  const carbEntriesCount = $derived(counts.carbs);

  let dailyAvgCarbs = $derived(totalCarbs / daysInRange);
  let dailyAvgBoluses = $derived(bolusCount / daysInRange);

  let avgInsulinPerBolus = $derived(
    bolusCount > 0 ? totalInsulin / bolusCount : 0
  );
  let avgCarbsPerEntry = $derived(
    carbEntriesCount > 0 ? totalCarbs / carbEntriesCount : 0
  );
</script>

<div class="grid grid-cols-1 md:grid-cols-3 gap-4">
  <!-- Total Records -->
  <Card.Root class="bg-card">
    <Card.Content class="p-4">
      <div class="flex items-center justify-between">
        <div>
          <p class="text-sm font-medium text-muted-foreground">
            Total Records
          </p>
          <p class="text-2xl font-bold tabular-nums">{counts.all}</p>
        </div>
        <div
          class="h-10 w-10 rounded-lg bg-primary/10 flex items-center justify-center"
        >
          <Activity class="h-5 w-5 text-primary" />
        </div>
      </div>
      <div class="mt-2 flex flex-wrap gap-1">
        {#each Object.entries(ENTRY_CATEGORIES) as [id, cat]}
          {#if counts[id as EntryCategoryId] > 0}
            <Badge variant="secondary" class="text-[10px] px-1.5 py-0">
              {cat.name} <span class="opacity-70">{counts[id as EntryCategoryId]}</span>
            </Badge>
          {/if}
        {/each}
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Insulin -->
  <Card.Root class="bg-card">
    <Card.Content class="p-4">
      <div class="flex items-center justify-between">
        <div>
          <p class="text-sm font-medium text-muted-foreground">Insulin</p>
          <p
            class="text-2xl font-bold tabular-nums text-[color:var(--insulin-bolus)]"
          >
            {totalInsulin.toFixed(1)}U
          </p>
        </div>
        <div
          class="h-10 w-10 rounded-lg bg-[color:var(--insulin-bolus)]/10 flex items-center justify-center"
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
  <Card.Root class="bg-card">
    <Card.Content class="p-4">
      <div class="flex items-center justify-between">
        <div>
          <p class="text-sm font-medium text-muted-foreground">Carbs</p>
          <p class="text-2xl font-bold tabular-nums text-[color:var(--carbs)]">
            {totalCarbs.toFixed(0)}g
          </p>
        </div>
        <div
          class="h-10 w-10 rounded-lg bg-[color:var(--carbs)]/10 flex items-center justify-center"
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
