<script lang="ts">
  import type { HTMLAttributes } from "svelte/elements";
  import { cn, type WithElementRef } from "../../../utils";
  import { cardDescriptionVariants, type CardDescriptionSize } from "./index.js";

  let {
    ref = $bindable(null),
    class: className,
    size = "default",
    children,
    ...restProps
  }: WithElementRef<HTMLAttributes<HTMLParagraphElement>> & {
    size?: CardDescriptionSize;
  } = $props();
</script>

<p
  bind:this={ref}
  data-slot="card-description"
  class={cn(cardDescriptionVariants({ size }), className)}
  {...restProps}
>
  {@render children?.()}
</p>
