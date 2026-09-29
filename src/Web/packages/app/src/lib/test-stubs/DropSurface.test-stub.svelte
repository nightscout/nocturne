<script lang="ts">
  import type { Snippet } from "svelte";

  // Paints nothing: stands in for the watercolour surface with its element, attributes and
  // content, and says whether the mark is held on.
  let {
    as = "div",
    shown,
    children,
    contentClass: _contentClass,
    name: _name,
    palette: _palette,
    ...rest
  }: {
    as?: string;
    shown?: boolean;
    children: Snippet;
    contentClass?: string;
    name?: string;
    palette?: string;
    [key: string]: unknown;
  } = $props();
</script>

<svelte:element this={as} {...rest} data-mark-shown={shown ? "true" : undefined}>
  {@render children()}
</svelte:element>
