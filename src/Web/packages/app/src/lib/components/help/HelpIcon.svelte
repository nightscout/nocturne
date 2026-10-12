<script lang="ts">
  import { page } from "$app/state";
  import Book from "@lucide/svelte/icons/book";
  import BookX from "@lucide/svelte/icons/book-x";
  import * as Tooltip from "$lib/components/ui/tooltip";
  import { Button } from "$lib/components/ui/button";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  import { useSidebar } from "$lib/components/ui/sidebar";
  const help = getHelpContext();
  const sidebar = useSidebar();
</script>

{#if help?.ready && help.mode !== "hidden"}
  <Tooltip.Provider>
    <Tooltip.Root>
      <Tooltip.Trigger>
        {#snippet child({ props })}
          <Button
            {...props}
            variant={help.open ? "secondary" : "ghost"}
            size="icon-sm"
            aria-label={help.open ? "Close page help" : "Open page help"}
            aria-pressed={help.open}
            aria-controls={help.open ? "nocturne-page-help" : undefined}
            onclick={(event) => {
              const header = event.currentTarget.closest(
                '[data-slot="sidebar-header"], header'
              );
              help.top = Math.max(
                0,
                header?.getBoundingClientRect().bottom ?? 64
              );
              help.toggle(page.url.pathname);
              sidebar?.setOpenMobile(false);
            }}
          >
            {#if help.open}<BookX class="size-4" />{:else}<Book
                class="size-4"
              />{/if}
          </Button>
        {/snippet}
      </Tooltip.Trigger><Tooltip.Content>
        {help.open ? "Close page help" : "Open page help"}
      </Tooltip.Content>
    </Tooltip.Root>
  </Tooltip.Provider>
{/if}
