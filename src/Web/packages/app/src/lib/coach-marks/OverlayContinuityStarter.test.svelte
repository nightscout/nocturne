<script lang="ts">
  import { getCoachMarkContext } from "@nocturne/coach";

  let { sequence }: { sequence: string } = $props();

  const ctx = getCoachMarkContext();
</script>

<button type="button" onclick={() => ctx.startSequence(sequence)}>Start tour</button>
