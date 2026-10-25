<script lang="ts">
  import { Button } from "$lib/components/ui/button";

  let {
    startError,
    onSettled,
  }: { startError?: string; onSettled?: () => void } = $props();
</script>

<p data-testid="start-error">{startError ?? ""}</p>
<Button onclick={() => onSettled?.()}>Stub: settle</Button>
