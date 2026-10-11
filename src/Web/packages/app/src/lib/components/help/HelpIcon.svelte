<script lang="ts">
  import { page } from "$app/state";
  import BookOpen from "@lucide/svelte/icons/book-open";
  import { Button } from "$lib/components/ui/button";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  import { useSidebar } from "$lib/components/ui/sidebar";
  const help = getHelpContext();
  const sidebar = useSidebar();
</script>

{#if help?.ready && help.mode !== "hidden"}
  <Button
    variant={help.open ? "secondary" : "ghost"}
    size="icon-sm"
    title={help.open ? "Close page help" : "Open page help"}
    aria-label={help.open ? "Close page help" : "Open page help"}
    aria-pressed={help.open}
    aria-controls={help.open ? "nocturne-page-help" : undefined}
    onclick={() => {
      help.toggle(page.url.pathname);
      sidebar?.setOpenMobile(false);
    }}
  >
    <BookOpen class="size-4" />
  </Button>
{/if}
