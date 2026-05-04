<script lang="ts">
  import { goto } from "$app/navigation";
  import { onMount } from "svelte";

  // Redirect from legacy alarms page to new alerts page
  onMount(() => {
    goto("/alerts", { replaceState: true });
  });
</script>

<div class="container mx-auto max-w-4xl p-6">
  <p class="text-muted-foreground">Redirecting to Alerts...</p>
</div>
