<script lang="ts">
  import { page } from "$app/state";
  import * as Alert from "$lib/components/ui/alert";
  import { Clock } from "lucide-svelte";

  let { class: className = "" }: { class?: string } = $props();
</script>

{#if page.data.limitTo24Hours}
  <Alert.Root variant="info" class={className}>
    <Clock />
    <Alert.Title>You can see the last 24 hours only</Alert.Title>
    <Alert.Description>
      The person who shared this data has limited you to the most recent 24
      hours. Anything older, including longer report periods, is not shown.
    </Alert.Description>
  </Alert.Root>
{/if}
