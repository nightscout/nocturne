<script lang="ts">
  import type { HTMLAttributes } from "svelte/elements";
  import { cn, type WithElementRef } from "../../../utils";
  import { cardTitleVariants, type CardTitleVariant } from "./index.js";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    children,
    ...restProps
  }: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
    variant?: CardTitleVariant;
  } = $props();
</script>

<div
  bind:this={ref}
  data-slot="card-title"
  class={cn(cardTitleVariants({ variant }), className)}
  {...restProps}
>
  {@render children?.()}
</div>
