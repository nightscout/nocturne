<script lang="ts">
  import { tick, type Snippet } from "svelte";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  import HelpBrowser from "./HelpBrowser.svelte";
  import HelpPanel from "./HelpPanel.svelte";
  import { Button } from "$lib/components/ui/button";
  const { children }: { children: Snippet } = $props();
  const help = getHelpContext();
  $effect(() => {
    if (!help?.blocking) return;
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
    if (help?.blocking && event.key === "Tab" && !event.defaultPrevented) {
      // A higher-priority app dialog (for example an alert) owns its own focus.
      if (
        event.target instanceof Element &&
        event.target.closest('[role="dialog"], [role="alertdialog"]') &&
        !event.target.closest("[data-help-panel]")
      )
        return;
      const panel = document.querySelector("[data-help-panel]");
      const focusable = Array.from(
        document.querySelectorAll<HTMLElement>(
          '[data-help-toggle], [data-help-panel] button, [data-help-panel] a[href], [data-help-panel] input, [data-help-panel] summary, [data-help-panel] [tabindex="0"]'
        )
      ).filter(
        (element) =>
          element.getClientRects().length > 0 &&
          !element.closest("[inert]") &&
          !element.hasAttribute("disabled")
      );
      if (!panel || !focusable.length) return;
      const active = document.activeElement;
      const index =
        active instanceof HTMLElement ? focusable.indexOf(active) : -1;
      event.preventDefault();
      const next =
        index < 0
          ? event.shiftKey
            ? focusable.length - 1
            : 0
          : (index + (event.shiftKey ? -1 : 1) + focusable.length) %
            focusable.length;
      focusable[next].focus({ preventScroll: true });
    }
  }}
  onfocusin={(event) => {
    if (!help?.blocking || !(event.target instanceof HTMLElement)) return;
    if (
      event.target.closest('[role="dialog"], [role="alertdialog"]') &&
      !event.target.closest("[data-help-panel]")
    )
      return;
    if (!event.target.closest("[data-help-panel], [data-help-toggle]")) {
      document
        .querySelector<HTMLElement>("[data-help-panel]")
        ?.focus({ preventScroll: true });
    }
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
    inert={help?.blocking || (help?.open && help.layout === "page")}
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
