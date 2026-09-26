<script lang="ts">
  import type { HTMLAttributes } from "svelte/elements";
  import { cn, type WithElementRef } from "../../../utils";
  import { cardVariants, type CardSize, type CardVariant } from "./index.js";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    size = "default",
    interactive = false,
    children,
    ...restProps
  }: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
    variant?: CardVariant;
    size?: CardSize;
    interactive?: boolean;
  } = $props();
</script>

<div
  bind:this={ref}
  data-slot="card"
  class={cn(cardVariants({ variant, size, interactive }), className)}
  {...restProps}
>
  {@render children?.()}
</div>
