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

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
  <div class="flex items-center justify-center py-12">
    <p class="text-muted-foreground">Redirecting to Profile...</p>
  </div>
</div>
