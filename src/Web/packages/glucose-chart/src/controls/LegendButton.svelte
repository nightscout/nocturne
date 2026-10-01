<script lang="ts">
  import type { Snippet } from "svelte";
  import { cn } from "../utils/formatting.js";

  /**
   * A legend entry that toggles a series. The package depends on no component library,
   * so this is its one button; the consumer's theme supplies the accent it hovers to.
   */
  interface Props {
    onclick: () => void;
    /** Whether the series is shown; a hidden one is dimmed. Omit for a button that opens something. */
    pressed?: boolean;
    /** Whether the menu this button opens is open. */
    expanded?: boolean;
    /** Which half of a split button this is: `start` holds the label, `end` a chevron. */
    segment?: "start" | "end";
    children: Snippet;
  }

  let { onclick, pressed, expanded, segment, children }: Props = $props();
</script>

<button
  type="button"
  aria-pressed={pressed}
  aria-expanded={expanded}
  class={cn(
    "flex items-center cursor-pointer hover:bg-accent/50 py-0.5 transition-colors",
    segment === "end" ? "px-0.5 rounded-r" : "gap-1 px-1.5",
    segment === "start" && "rounded-l",
    segment === undefined && "rounded",
    pressed === false && "opacity-50"
  )}
  {onclick}
>
  {@render children()}
</button>
