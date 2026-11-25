<script lang="ts">
  import * as Sheet from "./index.js";

  let { mounted = true }: { mounted?: boolean } = $props();
</script>

{#if mounted}
  <Sheet.Root open>
    <Sheet.Content>
      <Sheet.Title>Title</Sheet.Title>
      <Sheet.Description>Description</Sheet.Description>
    </Sheet.Content>
  </Sheet.Root>
{/if}
