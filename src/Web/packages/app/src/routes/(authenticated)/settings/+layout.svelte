<script lang="ts">
  import { page } from "$app/state";
  import { Button } from "$lib/components/ui/button";
  import { ChevronLeft } from "lucide-svelte";

  const { children } = $props();

  const isSubpage = $derived(page.url.pathname !== "/settings");
</script>

<div class="flex flex-col min-h-full">
  <!-- Breadcrumb navigation for subpages on mobile -->
  {#if isSubpage}
    <div class="md:hidden border-b border-border px-4 py-2">
      <Button variant="ghost" size="sm" href="/settings" class="-ml-2">
        <ChevronLeft class="h-4 w-4" />
        Settings
      </Button>
    </div>
  {/if}

  <!-- Main Content - No secondary sidebar, use the app's main sidebar -->
  <main class="flex-1 overflow-auto">
    {@render children()}
  </main>
</div>
