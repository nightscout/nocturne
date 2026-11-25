<!--
  The custom report pages. On a phone, every small button and toggle on them is at least 44px tall, so
  a finger can hit it; the controls keep their desktop size from the `sm` breakpoint up. A control set
  in a line of text (`data-touch-hit`) keeps its size and gets a 44px tall area to hit around it, so
  the text does not move. Text that holds such controls gets 44px lines, so when it wraps, the area
  of one line does not cover the controls of the line above.
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
    .touch-targets :global(:has(> [data-touch-hit])) {
      line-height: 2.75rem;
    }
    .touch-targets :global([data-touch-hit]) {
      position: relative;
    }
    .touch-targets :global([data-touch-hit])::after {
      content: "";
      position: absolute;
      inset-inline: 0;
      top: 50%;
      height: 2.75rem;
      translate: 0 -50%;
    }
  }
</style>
