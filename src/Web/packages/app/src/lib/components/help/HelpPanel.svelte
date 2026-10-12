<script lang="ts">
  import { fly } from "svelte/transition";
  import X from "@lucide/svelte/icons/x";
  import { Button } from "$lib/components/ui/button";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  import HelpBrowser from "./HelpBrowser.svelte";
  import HelpNavigation from "./HelpNavigation.svelte";
  import HelpAppearanceControls from "./HelpAppearanceControls.svelte";
  import * as Popover from "$lib/components/ui/popover";
  const help = getHelpContext();
  const bookLayout = $derived(
    help?.layout === "drawer" || help?.layout === "page"
  );
  function unfold(node: HTMLElement) {
    return fly(node, {
      x:
        help?.layout === "drawer" || help?.layout === "page"
          ? -node.getBoundingClientRect().width
          : 240,
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
  class:book-leaf={bookLayout}
  transition:unfold
>
  <header
    class={bookLayout
      ? "flex shrink-0 items-center justify-end gap-2 px-4 pt-4"
      : "flex shrink-0 items-center justify-between gap-3 border-b px-4 py-3"}
  >
    {#if !bookLayout}<div>
        <p class="font-semibold">Documentation</p>
        <p class="text-xs text-muted-foreground">
          Your page stays open underneath
        </p>
      </div>{/if}
    <Popover.Root>
      <Popover.Trigger>
        {#snippet child({ props })}<Button
            {...props}
            variant="outline"
            size="sm"
          >
            Display options
          </Button>{/snippet}
      </Popover.Trigger>
      <Popover.Content
        role="group"
        class="z-70"
        aria-label="Documentation appearance"
        data-help-controls
      >
        <HelpAppearanceControls />
      </Popover.Content>
    </Popover.Root>
    <Button
      variant={bookLayout ? "outline" : "ghost"}
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
      {#if !bookLayout || help?.seam === 0}
        <details class="border-b" open={bookLayout}>
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
    background: color-mix(
      in oklab,
      var(--background) var(--help-opacity, 78%),
      transparent
    );
    backdrop-filter: blur(var(--help-blur, 2px));
  }
  .book-leaf {
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
