<script lang="ts">
  import { fly } from "svelte/transition";
  import X from "@lucide/svelte/icons/x";
  import { Button } from "$lib/components/ui/button";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  import HelpBrowser from "./HelpBrowser.svelte";
  import HelpNavigation from "./HelpNavigation.svelte";
  const help = getHelpContext();
  function unfold(node: HTMLElement) {
    return fly(node, {
      x: help?.layout === "drawer" ? -node.getBoundingClientRect().width : 240,
      opacity: 1,
      duration: window.matchMedia("(prefers-reduced-motion: reduce)").matches
        ? 0
        : 2000,
    });
  }
  let panel: HTMLElement;
  let content: HTMLDivElement;
  $effect(() => {
    panel?.focus({ preventScroll: true });
  });
  $effect(() => {
    if (help?.path) content?.scrollTo({ top: 0 });
  });
</script>

<section
  data-help-panel
  bind:this={panel}
  tabindex="-1"
  aria-label="Documentation panel"
  class="help-panel flex min-h-0 flex-col text-foreground"
  class:book-leaf={help?.layout === "drawer"}
  transition:unfold
>
  <header
    class={help?.layout === "drawer"
      ? "flex shrink-0 items-center justify-end px-4 pt-4"
      : "flex shrink-0 items-center justify-between gap-3 border-b px-4 py-3"}
  >
    {#if help?.layout !== "drawer"}<div>
        <p class="font-semibold">Documentation</p>
        <p class="text-xs text-muted-foreground">
          Your page stays open underneath
        </p>
      </div>{/if}
    <Button
      variant={help?.layout === "drawer" ? "outline" : "ghost"}
      aria-label="Close documentation"
      onclick={() => {
        if (help) help.open = false;
      }}
    >
      <X class="size-4" />Close
    </Button>
  </header>
  <div class="flex min-h-0 flex-1" class:wide={help?.layout === "drawer"}>
    <div
      bind:this={content}
      class="min-w-0 flex-1 overflow-y-auto overscroll-contain"
    >
      {#if help?.layout !== "drawer" || help.seam === 0}
        <details class="border-b" open={help?.layout === "drawer"}>
          <summary class="cursor-pointer px-4 py-3 text-sm font-medium">
            Browse documentation
          </summary>
          <div class="compact-navigation overflow-y-auto overscroll-contain">
            <HelpNavigation />
          </div>
        </details>
      {/if}
      <HelpBrowser />
    </div>
  </div>
</section>

<style>
  .help-panel {
    height: 100%;
    outline: none;
    background: var(--background);
  }
  .book-leaf {
    background: color-mix(in oklab, var(--background) 88%, transparent);
    border-left: 1px solid var(--border);
  }
  .compact-navigation {
    max-height: 30dvh;
  }
  @media (max-width: 767px) {
    .wide {
      flex-direction: column;
    }
  }
</style>
