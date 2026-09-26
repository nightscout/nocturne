<script lang="ts">
  import { cn, type WithElementRef } from "../../../utils";
  import type { HTMLAttributes } from "svelte/elements";
  import { cardFooterVariants, type CardFooterVariant } from "./index.js";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    children,
    ...restProps
  }: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
    variant?: CardFooterVariant;
  } = $props();
</script>

<div
  bind:this={ref}
  data-slot="card-footer"
  class={cn(cardFooterVariants({ variant }), className)}
  {...restProps}
>
  {@render children?.()}
</div>
