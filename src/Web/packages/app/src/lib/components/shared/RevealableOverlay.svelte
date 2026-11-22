<script lang="ts">
  import type { Snippet } from "svelte";
  import type { HTMLAttributes } from "svelte/elements";
  import type { OverlayReveal } from "#lib/utils/overlay-reveal.svelte.js";

  interface Props extends HTMLAttributes<HTMLDivElement> {
    reveal: OverlayReveal;
    children: Snippet;
  }

  const { reveal, children, class: className = "", ...rest }: Props = $props();
</script>

<div
  {...rest}
  data-revealed={reveal.revealed}
  inert={reveal.inert}
  onclickcapture={reveal.clickCapture}
  class="fixed inset-x-0 top-0 z-50 flex items-center justify-between bg-linear-to-b from-black/50 to-transparent p-4 [&_button]:relative [&_button]:pointer-coarse:before:absolute [&_button]:pointer-coarse:before:-inset-1.5 [&_button]:pointer-coarse:before:content-[''] transition-opacity duration-300 focus-within:opacity-100 hover:opacity-100 {reveal.revealed
    ? 'opacity-100'
    : 'opacity-0 pointer-coarse:pointer-events-none'} {className}"
>
  {@render children()}
</div>
