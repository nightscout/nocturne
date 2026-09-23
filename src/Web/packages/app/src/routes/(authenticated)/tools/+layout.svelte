<script lang="ts">
  import { page } from "$app/state";
  import { Button } from "$lib/components/ui/button";
  import { ChevronLeft } from "lucide-svelte";

  const { children } = $props();
  const isSubpage = $derived(page.url.pathname !== "/tools");
</script>

<div class="flex flex-col min-h-full">
  {#if isSubpage}
    <div class="md:hidden border-b border-border px-4 py-2">
      <Button variant="ghost" size="sm" href="/tools" class="-ml-2">
        <ChevronLeft class="h-4 w-4" />
        Tools
      </Button>
    </div>
  {/if}

  <main class="flex-1 overflow-auto">
    {@render children()}
  </main>
</div>
