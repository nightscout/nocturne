<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { time, formatWeekdayDate } from "$lib/utils/formatting";

  const realtimeStore = getRealtimeStore();

  const currentTime = $derived(time(realtimeStore.now));
  const currentDate = $derived(formatWeekdayDate(realtimeStore.now));

  // Seconds for optional display
  const seconds = $derived.by(() => {
    const date = new Date(realtimeStore.now);
    return date.getSeconds().toString().padStart(2, "0");
  });
</script>

<WidgetCard title="Clock">
  <div class="flex flex-col items-center justify-center">
    <div class="text-2xl font-bold font-mono tabular-nums">
      {currentTime}<span class="text-lg text-muted-foreground">:{seconds}</span>
    </div>
    <p class="text-xs text-muted-foreground mt-1">{currentDate}</p>
  </div>
</WidgetCard>
