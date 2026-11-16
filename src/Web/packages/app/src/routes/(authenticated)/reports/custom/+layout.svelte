<!--
  The custom report pages. On a phone, every small button and toggle on them is at least 44px tall, so
  a finger can hit it; the controls keep their desktop size from the `sm` breakpoint up.
-->
<script lang="ts">
  import type { Snippet } from "svelte";

  let { children }: { children: Snippet } = $props();
</script>

<div class="touch-targets">
  {@render children()}
</div>

<style>
  @media (width < 40rem) {
    .touch-targets :global(:is(button, a):is(.h-7, .h-8, .size-7, .size-8)),
    .touch-targets :global([data-slot="toggle-group-item"]) {
      min-height: 2.75rem;
    }
    .touch-targets :global(:is(button, a):is(.size-7, .size-8)) {
      min-width: 2.75rem;
    }
  }
</style>
