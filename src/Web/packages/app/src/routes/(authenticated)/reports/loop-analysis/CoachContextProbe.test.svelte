<!-- Hands the provider's coach-mark context to the test. -->
<script lang="ts">
  import { getCoachMarkContext, type CoachMarkContext } from "@nocturne/coach";

  let { oncontext }: { oncontext: (ctx: CoachMarkContext) => void } = $props();

  // svelte-ignore state_referenced_locally
  oncontext(getCoachMarkContext());
</script>
