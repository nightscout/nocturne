<script lang="ts">
  import { goto } from "$app/navigation";
  import { onMount } from "svelte";

  // Redirect to the new Calendar page
  onMount(() => {
    goto("/calendar", { replaceState: true });
  });
</script>

<div class="flex items-center justify-center h-full">
  <p class="text-muted-foreground">Redirecting to Calendar...</p>
</div>
