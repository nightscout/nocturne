<script lang="ts">
  import type { HTMLAttributes } from "svelte/elements";
  import { cn, type WithElementRef } from "../../../utils";
  import { bannerVariants, type BannerVariant } from "./index.js";

  let {
    ref = $bindable(null),
    class: className,
    variant = "info",
    children,
    ...restProps
  }: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
    variant?: BannerVariant;
  } = $props();
</script>

<div
  bind:this={ref}
  data-slot="banner"
  class={cn(bannerVariants({ variant }), className)}
  {...restProps}
>
  {@render children?.()}
</div>
