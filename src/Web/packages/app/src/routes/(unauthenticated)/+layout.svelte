<script lang="ts">
  import AppLogo from "$lib/components/ui/AppLogo.svelte";

  let { children } = $props();
</script>

<div class="min-h-screen flex flex-col bg-background">
  <!-- Header -->
  <header class="border-b border-border/40">
    <div class="container mx-auto px-4 py-4 flex justify-center">
      <div class="flex items-center gap-2.5">
        <div class="w-8 h-8">
          <AppLogo icon="nocturne" invertMode />
        </div>
        <span class="text-lg font-semibold">Nocturne</span>
      </div>
    </div>
  </header>

  <!-- Content -->
  <main class="flex-1">
    {@render children()}
  </main>
</div>
