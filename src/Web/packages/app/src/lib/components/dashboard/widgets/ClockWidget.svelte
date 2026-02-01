<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { timeFormat } from "$lib/stores/appearance-store.svelte";

  const realtimeStore = getRealtimeStore();

  // Current time formatted based on user preference
  const currentTime = $derived.by(() => {
    const date = new Date(realtimeStore.now);
    const format = timeFormat.current;

    if (format === "24") {
      return date.toLocaleTimeString(undefined, {
        hour: "2-digit",
        minute: "2-digit",
        hour12: false,
      });
    }

    return date.toLocaleTimeString(undefined, {
      hour: "numeric",
      minute: "2-digit",
      hour12: true,
    });
  });

  // Current date
  const currentDate = $derived.by(() => {
    const date = new Date(realtimeStore.now);
    return date.toLocaleDateString(undefined, {
      weekday: "short",
      month: "short",
      day: "numeric",
    });
  });

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
