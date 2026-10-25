<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import ConnectionStatus from "$lib/components/ConnectionStatus.svelte";
</script>

<WidgetCard title="Connection">
  <ConnectionStatus />
</WidgetCard>
