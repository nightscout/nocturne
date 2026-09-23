<script lang="ts">
  import { cn, type WithElementRef } from "../../../utils";
  import type { HTMLThAttributes } from "svelte/elements";

  let {
    ref = $bindable(null),
    class: className,
    children,
    ...restProps
  }: WithElementRef<HTMLThAttributes> = $props();
</script>

<th
  bind:this={ref}
  data-slot="table-head"
  class={cn(
    "text-foreground h-10 whitespace-nowrap px-2 text-left align-middle font-medium [&:has([role=checkbox])]:pr-0 print:h-auto print:py-1 print:text-xs",
    className
  )}
  {...restProps}
>
  {@render children?.()}
</th>
