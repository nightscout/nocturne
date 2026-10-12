<script lang="ts">
  import { tick } from "svelte";
  import { fly } from "svelte/transition";
  import X from "@lucide/svelte/icons/x";
  import { Button } from "$lib/components/ui/button";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  import HelpBrowser from "./HelpBrowser.svelte";
  import HelpNavigation from "./HelpNavigation.svelte";
  const help = getHelpContext();
  let panel: HTMLElement;
  let content: HTMLDivElement;
  $effect(() => {
    const previous = document.activeElement;
    panel?.focus({ preventScroll: true });
    return () => {
      void tick().then(() => {
        if (previous instanceof HTMLElement && previous.isConnected)
          previous.focus({ preventScroll: true });
      });
    };
  });
  $effect(() => {
    if (help?.path) content?.scrollTo({ top: 0 });
  });
</script>

<section
  bind:this={panel}
  tabindex="-1"
  aria-label="Documentation panel"
  class="help-panel flex min-h-0 flex-col bg-background text-foreground"
  transition:fly={{
    x: help?.layout === "drawer" ? -240 : 240,
    duration:
      typeof window !== "undefined" &&
      window.matchMedia("(prefers-reduced-motion: reduce)").matches
        ? 0
        : 200,
  }}
>
  <header
    class="flex shrink-0 items-center justify-between gap-3 border-b px-4 py-3"
  >
    <div>
      <p class="font-semibold">Documentation</p>
      <p class="text-xs text-muted-foreground">
        Your page stays open underneath
      </p>
    </div>
    <Button
      variant="ghost"
      aria-label="Close documentation"
      onclick={() => {
        if (help) help.open = false;
      }}
    >
      <X class="size-4" />Close
    </Button>
  </header>
  <div
    class="flex min-h-0 flex-1"
    class:wide={help?.layout === "drawer"}
  >
    {#if help?.layout === "drawer"}
      <div class="help-nav min-h-0 overflow-y-auto overscroll-contain border-r bg-sidebar">
        <HelpNavigation />
      </div>
    {/if}
    <div
      bind:this={content}
      class="min-w-0 flex-1 overflow-y-auto overscroll-contain"
    >
      {#if help?.layout !== "drawer"}
        <details class="border-b">
          <summary class="cursor-pointer px-4 py-3 text-sm font-medium">
            Browse documentation
          </summary>
          <HelpNavigation />
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
  }
  .help-nav {
    width: 16rem;
    flex-shrink: 0;
  }
  @media (max-width: 767px) {
    .wide {
      flex-direction: column;
    }
    .help-nav {
      width: 100%;
      max-height: 30%;
      border-right: 0;
      border-bottom: 1px solid var(--border);
    }
  }
</style>
