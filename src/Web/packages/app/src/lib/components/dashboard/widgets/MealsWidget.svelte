<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { time } from "$lib/utils/formatting";
  import { EmptyState } from "$lib/components/shared";

  const realtimeStore = getRealtimeStore();

  // Get recent carb intakes from the last 6 hours
  const recentMeals = $derived.by(() => {
    const sixHoursAgo = realtimeStore.now - 6 * 60 * 60 * 1000;
    return realtimeStore.carbIntakes
      .filter((c) => (c.mills ?? 0) > sixHoursAgo && (c.carbs ?? 0) > 0)
      .sort((a, b) => (b.mills ?? 0) - (a.mills ?? 0))
      .slice(0, 3);
  });

  // Total carbs in the period
  const totalCarbs = $derived(
    recentMeals.reduce((sum, c) => sum + (c.carbs ?? 0), 0)
  );

  // Last meal time
  const lastMealTime = $derived(recentMeals[0]?.mills);
</script>

<WidgetCard title="Recent Meals">
  {#if recentMeals.length > 0}
    <div class="space-y-2">
      <div class="flex items-center justify-between">
        <span class="text-2xl font-bold">{totalCarbs}g</span>
        <Badge variant="secondary">
          {recentMeals.length} meal{recentMeals.length !== 1 ? "s" : ""}
        </Badge>
      </div>
      <div class="text-xs text-muted-foreground">
        {#if lastMealTime}
          Last: {time(lastMealTime)}
        {/if}
      </div>
      <div class="flex flex-wrap gap-1 mt-1">
        {#each recentMeals.slice(0, 3) as carbIntake (carbIntake.id)}
          <Badge variant="outline">
            {carbIntake.carbs ?? 0}g
          </Badge>
        {/each}
      </div>
    </div>
  {:else}
    <EmptyState art="apple" size="compact" title="No recent meals" />
  {/if}
</WidgetCard>
