<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { onMount } from "svelte";

  // Redirect to the new Calendar page
  onMount(() => {
    goto(resolve("/calendar"), { replaceState: true });
  });
</script>

<div class="flex items-center justify-center h-full">
  <p class="text-muted-foreground">Redirecting to Calendar...</p>
</div>
