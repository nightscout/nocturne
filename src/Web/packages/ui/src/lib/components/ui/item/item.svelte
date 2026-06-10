<script lang="ts">
  import { cn } from "../../../utils";
  import type { Snippet } from "svelte";
  import type { HTMLAttributes } from "svelte/elements";
  import {
    itemVariants,
    type ItemVariant,
    type ItemSize,
  } from "./item-variants";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    size = "default",
    children,
    child,
    ...restProps
  }: HTMLAttributes<HTMLDivElement> & {
    ref?: HTMLDivElement | null;
    variant?: ItemVariant;
    size?: ItemSize;
    child?: Snippet<[{ props: Record<string, unknown> }]>;
  } = $props();

  const mergedClasses = $derived(
    cn(itemVariants({ variant, size }), className)
  );
</script>

{#if child}
  {@render child({ props: { class: mergedClasses, ...restProps } })}
{:else}
  <div bind:this={ref} class={mergedClasses} {...restProps}>
    {@render children?.()}
  </div>
{/if}
