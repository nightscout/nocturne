<script lang="ts">
  import { page } from "$app/state";
  import BookOpen from "@lucide/svelte/icons/book-open";
  import { Button } from "$lib/components/ui/button";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  const help = getHelpContext();
</script>

{#if help?.ready && help.mode === "icon"}
  <Button
    variant="ghost"
    size="icon-sm"
    title="Open page help"
    aria-label="Open page help"
    onclick={() => help.show(page.url.pathname)}
  >
    <BookOpen class="size-4" />
  </Button>
{/if}
