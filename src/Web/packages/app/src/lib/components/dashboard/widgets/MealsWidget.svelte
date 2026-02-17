<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { formatTime } from "$lib/utils";
  import { UtensilsCrossed } from "lucide-svelte";

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
        <Badge variant="secondary" class="text-xs">
          {recentMeals.length} meal{recentMeals.length !== 1 ? "s" : ""}
        </Badge>
      </div>
      <div class="text-xs text-muted-foreground">
        {#if lastMealTime}
          Last: {formatTime(lastMealTime)}
        {/if}
      </div>
      <div class="flex flex-wrap gap-1 mt-1">
        {#each recentMeals.slice(0, 3) as carbIntake}
          <Badge variant="outline" class="text-xs">
            {carbIntake.carbs ?? 0}g
          </Badge>
        {/each}
      </div>
    </div>
  {:else}
    <div class="flex flex-col items-center justify-center text-muted-foreground py-2">
      <UtensilsCrossed class="h-6 w-6 mb-1 opacity-50" />
      <p class="text-xs">No recent meals</p>
    </div>
  {/if}
</WidgetCard>
