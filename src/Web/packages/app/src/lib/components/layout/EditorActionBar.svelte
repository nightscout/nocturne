<script lang="ts">
  import type { Snippet } from "svelte";

  // Header for save-gated editors. The save action stays on screen while the
  // form scrolls: the header is sticky at the top on md+, and on small screens
  // the actions container is taken out of flow and pinned to the bottom of the
  // viewport (where the thumb is and clear of the fixed top MobileHeader). One
  // responsive markup, no separate mobile variant.
  //
  // The `actions` snippet is rendered exactly once, so an `id`, `bind:this` or
  // `autofocus` inside it stays unique.
  //
  // Consuming pages must leave room for the mobile bottom bar by adding
  // `max-md:pb-24` to their scroll container.
  let {
    leading,
    actions,
  }: {
    leading?: Snippet;
    actions: Snippet;
  } = $props();
</script>

<div
  class="mb-6 flex items-center justify-between gap-4 md:sticky md:top-0 md:z-30 md:border-b md:border-border/60 md:bg-background/95 md:py-3 md:backdrop-blur"
>
  <div class="flex min-w-0 items-center gap-2">
    {@render leading?.()}
  </div>
  <!-- md+: in the sticky header. Below md: pinned to the bottom of the viewport -->
  <div
    class="flex shrink-0 items-center justify-end gap-2 max-md:fixed max-md:inset-x-0 max-md:bottom-0 max-md:z-40 max-md:border-t max-md:border-border max-md:bg-background/95 max-md:px-4 max-md:py-3 max-md:backdrop-blur"
  >
    {@render actions()}
  </div>
</div>
