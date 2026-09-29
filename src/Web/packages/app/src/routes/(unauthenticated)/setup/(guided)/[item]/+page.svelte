<script lang="ts">
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import { Button } from "$lib/components/ui/button";
  import { SETUP_HUB_ITEMS } from "$lib/setup-hub/items.svelte";
  import type { PageData } from "./$types";

  let { data }: { data: PageData } = $props();

  const view = $derived(SETUP_HUB_ITEMS[data.item]);
</script>

<div class="flex flex-col items-start gap-4">
  <p class="text-sm leading-relaxed text-muted-foreground">
    A guided version of this step is on its way. Until then, you can set it up on its settings
    page. It is marked done here once it is working.
  </p>
  <Button href={view.settingsHref}>
    Open settings
    <ArrowRight class="h-4 w-4" />
  </Button>
</div>
