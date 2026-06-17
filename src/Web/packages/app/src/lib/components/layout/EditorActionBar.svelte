<script lang="ts">
  import type { Snippet } from "svelte";

  // Header for save-gated editors. The save action stays on screen while the
  // form scrolls: the header is sticky at the top on md+, and on small screens
  // the actions move to a bar pinned at the bottom of the viewport (where the
  // thumb is and clear of the fixed top MobileHeader). One responsive markup,
  // no separate mobile variant.
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
  <!-- Desktop: actions live in the sticky header -->
  <div class="hidden shrink-0 items-center gap-2 md:flex">
    {@render actions()}
  </div>
</div>

<!-- Mobile: actions pinned to the bottom of the viewport -->
<div
  class="fixed inset-x-0 bottom-0 z-40 flex items-center justify-end gap-2 border-t border-border bg-background/95 px-4 py-3 backdrop-blur md:hidden"
>
  {@render actions()}
</div>
