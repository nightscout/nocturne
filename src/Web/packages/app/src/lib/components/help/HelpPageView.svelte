<script lang="ts">
  import { onMount, tick, type Snippet } from "svelte";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  import HelpBrowser from "./HelpBrowser.svelte";
  import HelpPanel from "./HelpPanel.svelte";
  import { Button } from "$lib/components/ui/button";
  const { children }: { children: Snippet } = $props();
  const help = getHelpContext();
  let compact = $state(false);
  onMount(() => {
    const media = window.matchMedia("(max-width: 1100px)");
    const sync = () => {
      compact = media.matches;
    };
    sync();
    media.addEventListener("change", sync);
    return () => media.removeEventListener("change", sync);
  });
  const blocking = $derived(
    help?.open && (help.layout !== "alongside" || compact)
  );
  $effect(() => {
    if (!blocking || help?.layout === "page") return;
    const overflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = overflow;
    };
  });
  $effect(() => {
    if (!help?.open || help.layout !== "page") return;
    const scrollY = window.scrollY;
    window.scrollTo({ top: 0, behavior: "instant" });
    return () => {
      void tick().then(() => {
        if (!help.open) window.scrollTo({ top: scrollY, behavior: "instant" });
      });
    };
  });
</script>

<svelte:window
  onkeydown={(event) => {
    if (help?.open && event.key === "Escape" && !event.defaultPrevented)
      help.open = false;
  }}
/>

{#if help?.open && help.layout === "page"}
  <div class="flex justify-end px-4 pt-4">
    <Button
      variant="outline"
      onclick={() => {
        if (help) help.open = false;
      }}
    >
      Close documentation
    </Button>
  </div>
  <HelpBrowser />
{/if}
<!-- Keep page controls and loaded data mounted while documentation replaces their view. -->
<div
  class={help?.open && help.layout === "alongside" ? "alongside" : "contents"}
  style="--help-top: {help?.top ?? 64}px"
>
  <div
    hidden={help?.open && help.layout === "page"}
    inert={blocking}
    class={help?.open && help.layout === "page"
      ? "hidden"
      : help?.open && help.layout === "alongside"
        ? "min-w-0"
        : "contents"}
  >
    {@render children()}
  </div>
  {#if help?.open && help.layout !== "page"}
    <div
      class="help-surface"
      class:drawer={help.layout === "drawer"}
      class:right={help.layout === "right"}
      class:docked={help.layout === "alongside"}
    >
      <HelpPanel />
    </div>
  {/if}
</div>

<style>
  .help-surface {
    position: fixed;
    z-index: 60;
    top: var(--help-top);
    bottom: 0;
    right: 0;
    border: 1px solid var(--border);
    box-shadow: -12px 0 32px #0003;
  }
  .drawer {
    left: 0;
  }
  .right {
    width: min(36rem, 100%);
  }
  .alongside {
    display: grid;
    min-height: 100dvh;
    grid-template-columns: minmax(0, 1fr) minmax(24rem, 38%);
    align-items: start;
  }
  .docked {
    position: sticky;
    height: calc(100dvh - var(--help-top));
  }
  @media (max-width: 1100px) {
    .alongside {
      display: block;
    }
    .docked {
      position: fixed;
      width: 100%;
      height: auto;
    }
  }
  @media print {
    .help-surface {
      display: none;
    }
  }
</style>
