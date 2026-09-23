<script lang="ts">
  import type { HTMLAttributes } from "svelte/elements";
  import { cn, type WithElementRef } from "../../../utils";
  import {
    cardContentVariants,
    type CardContentSize,
    type CardContentVariant,
  } from "./index.js";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    size = "default",
    children,
    ...restProps
  }: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
    variant?: CardContentVariant;
    size?: CardContentSize;
  } = $props();
</script>

<div
  bind:this={ref}
  data-slot="card-content"
  class={cn(cardContentVariants({ variant, size }), className)}
  {...restProps}
>
  {@render children?.()}
</div>
