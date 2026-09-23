<script lang="ts">
  import type { HTMLAttributes } from "svelte/elements";
  import { cn, type WithElementRef } from "../../../utils";
  import { cardVariants, type CardVariant } from "./index.js";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    interactive = false,
    children,
    ...restProps
  }: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
    variant?: CardVariant;
    interactive?: boolean;
  } = $props();
</script>

<div
  bind:this={ref}
  data-slot="card"
  class={cn(cardVariants({ variant, interactive }), className)}
  {...restProps}
>
  {@render children?.()}
</div>
