<script lang="ts">
  import { cn } from "../../../utils";
  import type { HTMLAttributes } from "svelte/elements";
  import {
    itemMediaVariants,
    type ItemMediaVariant,
  } from "./item-media-variants";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    children,
    ...restProps
  }: HTMLAttributes<HTMLDivElement> & {
    ref?: HTMLDivElement | null;
    variant?: ItemMediaVariant;
  } = $props();
</script>

<div
  bind:this={ref}
  class={cn(itemMediaVariants({ variant }), className)}
  {...restProps}
>
  {@render children?.()}
</div>
