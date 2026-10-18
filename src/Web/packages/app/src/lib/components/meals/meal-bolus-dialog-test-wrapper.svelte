<script lang="ts">
  // Test-only wrapper: puts a realtime store in context above the dialog.
  import { createRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import type { RealtimeStore } from "$lib/stores/realtime-store.svelte";
  import type { MealEvent } from "$lib/api";
  import MealBolusDialog from "./MealBolusDialog.svelte";

  let {
    meal,
    onstore,
    onSave,
  }: {
    meal: MealEvent;
    onstore: (store: RealtimeStore) => void;
    onSave: () => void;
  } = $props();

  // Read once, deliberately: the test hands the callback in at mount.
  // svelte-ignore state_referenced_locally
  onstore(
    createRealtimeStore({
      url: "",
      reconnectAttempts: 0,
      reconnectDelay: 0,
      maxReconnectDelay: 0,
      pingTimeout: 0,
      pingInterval: 0,
    })
  );
</script>

<MealBolusDialog open={true} onOpenChange={() => {}} {meal} {onSave} />
