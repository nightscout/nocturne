<script lang="ts">
  import { page } from "$app/state";
  import BookOpen from "@lucide/svelte/icons/book-open";
  import X from "@lucide/svelte/icons/x";
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
            data-help-toggle
            aria-controls={help.open ? "nocturne-page-help" : undefined}
            onclick={() => {
              help.toggle(page.url.pathname);
              sidebar?.setOpenMobile(false);
            }}
          >
            <span class="relative">
              <BookOpen class="size-4" />{#if help.open}<X
                  class="absolute -right-1 -top-1 size-2.5"
                  strokeWidth={3}
                />{/if}
            </span>
          </Button>
        {/snippet}
      </Tooltip.Trigger><Tooltip.Content>
        {help.open ? "Close page help" : "Open page help"}
      </Tooltip.Content>
    </Tooltip.Root>
  </Tooltip.Provider>
{/if}
