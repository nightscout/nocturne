<script lang="ts">
  import { tick, type Snippet } from "svelte";
  import { Key } from "@nocturne/ui/keys";
  import { on } from "svelte/events";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  import HelpPanel from "./HelpPanel.svelte";
  import { fly } from "svelte/transition";
  const { children }: { children: Snippet } = $props();
  const help = getHelpContext();
  // @wc-ignore
  const tabKey = "Tab";
  $effect(() => {
    if (!help?.open) return;
    const active = document.activeElement;
    const book = Array.from(
      document.querySelectorAll<HTMLElement>("[data-help-toggle]")
    ).find((element) => element.getClientRects().length > 0);
    const previous =
      active instanceof HTMLElement &&
      active !== document.body &&
      active.isConnected &&
      !active.closest("[data-help-panel]")
        ? active
        : book;
    return () => {
      void tick().then(() => {
        if (!help.open && previous?.isConnected)
          previous.focus({ preventScroll: true });
      });
    };
  });
  $effect(() => {
    if (!help?.open) return;
    const root = document.documentElement;
    const body = document.body;
    const overflow = body.style.overflow;
    const rootOverflow = root.style.overflow;
    const gutter = root.style.scrollbarGutter;
    const { scrollX, scrollY } = window;
    root.style.scrollbarGutter =
      window.innerWidth > root.clientWidth ? "stable" : gutter;
    root.style.overflow = "hidden";
    body.style.overflow = "hidden";
    // Inner page/sidebar scrollers must not receive wheel or touch gestures either.
    const preventBackgroundScroll = (event: Event) => {
      const insideHelp = event
        .composedPath()
        .some(
          (target) =>
            target instanceof Element &&
            target.matches(
              "[data-help-panel], [data-help-navigation], [data-help-controls], [role=dialog], [role=alertdialog]"
            ) &&
            !target.closest('[inert], [aria-hidden="true"]') &&
            target.getClientRects().length > 0
        );
      if (!insideHelp && event.cancelable) event.preventDefault();
    };
    const offWheel = on(window, "wheel", preventBackgroundScroll, {
      passive: false,
    });
    const offTouch = on(window, "touchmove", preventBackgroundScroll, {
      passive: false,
    });
    return () => {
      offWheel();
      offTouch();
      body.style.overflow = overflow;
      root.style.overflow = rootOverflow;
      root.style.scrollbarGutter = gutter;
      window.scrollTo({ left: scrollX, top: scrollY, behavior: "instant" });
    };
  });
</script>

<svelte:window
  onkeydown={(event) => {
    if (help?.open && event.key === Key.Escape && !event.defaultPrevented)
      help.open = false;
    if (help?.blocking && event.key === tabKey && !event.defaultPrevented) {
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
          '[data-help-toggle], [data-help-panel] button, [data-help-panel] a[href], [data-help-panel] input, [data-help-panel] summary, [data-help-panel] [tabindex="0"], [data-help-navigation] button, [data-help-navigation] a[href], [data-help-controls] button, [data-help-controls] [tabindex="0"]'
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
    if (
      !event.target.closest(
        "[data-help-panel], [data-help-toggle], [data-help-navigation], [data-help-controls]"
      )
    ) {
      document
        .querySelector<HTMLElement>("[data-help-panel]")
        ?.focus({ preventScroll: true });
    }
  }}
/>

<!-- Keep page controls and loaded data mounted while documentation replaces their view. -->
<div
  class={help?.open && help.layout === "page"
    ? "page-stack"
    : help?.open && help.layout === "alongside"
      ? "alongside"
      : "contents"}
  style="--help-top: {help?.top ?? 64}px; --help-seam: {help?.seam ??
    0}px; --help-opacity: {100 -
    (help?.transparency ?? 22)}%; --help-blur: {help?.blur ?? 2}px"
>
  <div
    inert={help?.blocking || (help?.open && help.layout === "page")}
    class={help?.open && help.layout === "alongside" ? "min-w-0" : "contents"}
  >
    {@render children()}
  </div>
  {#if help?.open && (help.layout === "drawer" || help.layout === "page") && help.seam > 0}
    <div class="help-menu-leaf" aria-hidden="true">
      <div
        class="help-menu-glass"
        transition:fly={{
          x: help.seam,
          opacity: 1,
          duration: window.matchMedia("(prefers-reduced-motion: reduce)")
            .matches
            ? 0
            : 2000,
        }}
      ></div>
    </div>
  {/if}
  {#if help?.open}
    <div
      class="help-surface"
      class:drawer={help.layout === "drawer"}
      class:page={help.layout === "page"}
      class:right={help.layout === "right"}
      class:docked={help.layout === "alongside"}
    >
      <HelpPanel />
    </div>
  {/if}
</div>

<style>
  .page-stack {
    position: relative;
  }
  .help-surface.page {
    position: fixed;
    z-index: 20;
    top: var(--help-top);
    left: var(--help-seam);
    border: 0;
    box-shadow: none;
    overflow: clip;
  }
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
    left: var(--help-seam);
    overflow: clip;
    border: 0;
    box-shadow: none;
  }
  .help-menu-leaf {
    position: fixed;
    z-index: 60;
    left: 0;
    top: var(--help-top);
    bottom: 0;
    width: var(--help-seam);
    overflow: clip;
    pointer-events: none;
  }
  .help-menu-glass {
    height: 100%;
    background: color-mix(in oklab, var(--primary) 6%, transparent);
    border-right: 1px solid var(--border);
  }
  .right {
    width: min(36rem, 100%);
  }
  .alongside {
    display: grid;
    grid-template-columns: minmax(0, 1fr) minmax(24rem, 38%);
    align-items: start;
  }
  .docked {
    position: fixed;
    width: max(24rem, calc((100vw - var(--help-seam)) * 0.38));
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
    .help-surface,
    .help-menu-leaf {
      display: none;
    }
  }
</style>
