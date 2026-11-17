<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";

  let { onComplete }: { onComplete?: () => void } = $props();
</script>

<Button onclick={() => onComplete?.()}>Stub: complete</Button>
