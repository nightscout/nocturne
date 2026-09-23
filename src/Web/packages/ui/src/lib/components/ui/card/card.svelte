<script lang="ts">
  import type { HTMLAttributes } from "svelte/elements";
  import { cn, type WithElementRef } from "../../../utils";
  import { cardVariants, type CardVariant } from "./index.js";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    children,
    ...restProps
  }: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
    variant?: CardVariant;
  } = $props();
</script>

<div
  bind:this={ref}
  data-slot="card"
  class={cn(cardVariants({ variant }), className)}
  {...restProps}
>
  {@render children?.()}
</div>
