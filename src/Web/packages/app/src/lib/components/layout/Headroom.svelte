<!--
  Sticky content that scrolls away with the page and slides back in once the page has been
  scrolled up by `revealAfter` pixels, pinned at --headroom-top, which the surrounding layout
  sets beneath its own sticky chrome.
-->
<script lang="ts">
  import type { Snippet } from "svelte";

  let {
    children,
    revealAfter = 50,
    class: className = "",
  }: {
    children: Snippet;
    /** How far the page must scroll up, without scrolling down, before the panel returns. */
    revealAfter?: number;
    class?: string;
  } = $props();

  let sentinel = $state<HTMLDivElement>();
  let panel = $state<HTMLDivElement>();
  let revealed = $state(false);
  /** Pixels the stuck panel is slid up out of view. */
  let hidden = $state(0);
  let sliding = $state(false);
  let lastScrollY = 0;
  let scrolledUp = 0;

  /**
   * How far the page has scrolled past the panel's place in the flow. The panel is never slid
   * further than this, so it scrolls with the page near the top and comes to rest in the flow.
   */
  function travelled() {
    if (!sentinel || !panel) return 0;
    return Math.max(0, pinnedTop() - sentinel.getBoundingClientRect().top);
  }

  function pinnedTop() {
    return panel ? parseFloat(getComputedStyle(panel).top) || 0 : 0;
  }

  /** Far enough to clear whatever it pins beneath, which may be translucent. */
  function hideDistance() {
    return (panel?.offsetHeight ?? 0) + pinnedTop();
  }

  /** Animates only a change of `revealed`; tracking the page as it scrolls stays immediate. */
  function place(nextRevealed = revealed) {
    const next = nextRevealed ? 0 : Math.min(travelled(), hideDistance());
    if (nextRevealed !== revealed && next !== hidden) sliding = true;
    revealed = nextRevealed;
    hidden = next;
  }

  function onscroll() {
    const delta = window.scrollY - lastScrollY;
    lastScrollY = window.scrollY;
    let next = revealed;
    if (delta > 0) {
      scrolledUp = 0;
      next = false;
    } else if (delta < 0) {
      scrolledUp -= delta;
      if (scrolledUp >= revealAfter) next = true;
    }
    place(next);
  }
</script>

<svelte:window {onscroll} />

<div bind:this={sentinel} aria-hidden="true"></div>
<div
  bind:this={panel}
  class={[
    "sticky top-(--headroom-top,0px) z-10 translate-y-(--headroom-offset) print:static print:translate-y-0",
    sliding && "transition-transform duration-200 ease-out motion-reduce:transition-none",
    className,
  ]}
  style:--headroom-offset="{-hidden}px"
  ontransitionend={(event) => {
    if (event.target === event.currentTarget) sliding = false;
  }}
  ontransitioncancel={(event) => {
    if (event.target === event.currentTarget) sliding = false;
  }}
  onfocusin={() => place(true)}
>
  {@render children()}
</div>
