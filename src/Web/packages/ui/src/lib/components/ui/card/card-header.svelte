<script lang="ts">
  import { cn, type WithElementRef } from "../../../utils";
  import type { HTMLAttributes } from "svelte/elements";
  import { cardHeaderVariants } from "./index.js";

  let {
    ref = $bindable(null),
    class: className,
    interactive = false,
    children,
    ...restProps
  }: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
    interactive?: boolean;
  } = $props();
</script>

<div
  bind:this={ref}
  data-slot="card-header"
  class={cn(cardHeaderVariants({ interactive }), className)}
  {...restProps}
>
  {@render children?.()}
</div>
