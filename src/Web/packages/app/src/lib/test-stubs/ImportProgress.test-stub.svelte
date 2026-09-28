<script lang="ts">
  import { Button } from "$lib/components/ui/button";

  let { onSettled }: { onSettled?: () => void } = $props();
</script>

<Button onclick={() => onSettled?.()}>Stub: settle</Button>
