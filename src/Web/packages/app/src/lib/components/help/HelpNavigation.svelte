<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  const help = getHelpContext();
</script>

<nav aria-label="Documentation navigation" class="space-y-1 p-3">
  {#each help?.navigation ?? [] as item (item.id)}
    {#if item.children}
      <details open={item.children.some((child) => child.href === help?.path)}>
        <summary
          class="flex cursor-pointer items-center gap-2 rounded-md p-2 text-sm font-medium hover:bg-accent"
        >
          <item.icon class="size-4" />{item.title}
        </summary>
        <div class="ml-3 space-y-1 border-l pl-2">
          {#each item.children as child (child.href)}
            <Button
              variant={help?.path === child.href ? "secondary" : "ghost"}
              class="w-full justify-start"
              aria-current={help?.path === child.href ? "page" : undefined}
              onclick={() => help?.select(child.href)}
            >
              <child.icon class="size-4 shrink-0" />
              <span class="truncate">{child.title}</span>
            </Button>
          {/each}
        </div>
      </details>
    {:else if item.href}
      <Button
        variant={help?.path === item.href ? "secondary" : "ghost"}
        class="w-full justify-start"
        aria-current={help?.path === item.href ? "page" : undefined}
        onclick={() => help?.select(item.href!)}
      >
        <item.icon class="size-4 shrink-0" />{item.title}
      </Button>
    {/if}
  {/each}
</nav>
