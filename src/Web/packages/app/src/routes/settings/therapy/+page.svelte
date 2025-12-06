<script lang="ts">
  import { goto } from "$app/navigation";
  import { onMount } from "svelte";

  // Redirect to profile page since therapy settings are managed there
  // This eliminates the redundancy between therapy settings and profiles
  onMount(() => {
    goto("/profile", { replaceState: true });
  });
</script>

<svelte:head>
  <title>Therapy - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-3xl">
  <div class="flex items-center justify-center py-12">
    <p class="text-muted-foreground">Redirecting to Profile...</p>
  </div>
</div>
