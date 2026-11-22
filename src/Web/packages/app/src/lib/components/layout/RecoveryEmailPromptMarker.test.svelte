<script lang="ts">
  import RecoveryEmailPrompt from "./RecoveryEmailPrompt.svelte";

  let { show }: { show: boolean } = $props();
</script>

<p>marker</p>
<RecoveryEmailPrompt {show} />
