<script lang="ts">
  // Mounts the real chart card with a glucose window on hand, so whether the plot is drawn
  // turns on `deferDrawing` alone.
  import { createRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { transformChartData } from "$lib/utils/chart-data-transform";
  import GlucoseChartCard from "./GlucoseChartCard.svelte";

  interface Props {
    initiallyDeferred: boolean;
    showPredictions?: boolean;
    /** Narrower than a phone, so the header's controls take their own line. */
    narrow?: boolean;
    /** Hands the test the setter for `deferDrawing`. */
    onready: (setDeferred: (deferred: boolean) => void) => void;
  }

  let { initiallyDeferred, showPredictions = false, narrow = false, onready }: Props = $props();

  createRealtimeStore({
    url: "",
    reconnectAttempts: 0,
    reconnectDelay: 0,
    maxReconnectDelay: 0,
    pingTimeout: 0,
    pingInterval: 0,
  });

  const now = Date.now();
  const initialChartData = transformChartData({
    glucoseData: [
      { time: now - 10 * 60 * 1000, sgv: 110 },
      { time: now - 5 * 60 * 1000, sgv: 120 },
    ],
  });

  // svelte-ignore state_referenced_locally
  let deferred = $state(initiallyDeferred);
  // svelte-ignore state_referenced_locally
  onready((value) => (deferred = value));
</script>

<div class="h-[600px] {narrow ? 'w-[340px]' : 'w-[800px]'}">
  <GlucoseChartCard {initialChartData} {showPredictions} deferDrawing={deferred} />
</div>
