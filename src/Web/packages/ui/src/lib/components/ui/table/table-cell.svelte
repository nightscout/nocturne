<script lang="ts">
  import { cn, type WithElementRef } from "../../../utils";
  import type { HTMLTdAttributes } from "svelte/elements";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    children,
    ...restProps
  }: WithElementRef<HTMLTdAttributes> & {
    /** muted: a secondary column, or the message of an empty table. */
    variant?: "default" | "muted";
  } = $props();
</script>

<td
  bind:this={ref}
  data-slot="table-cell"
  class={cn(
    "whitespace-nowrap p-2 align-middle [&:has([role=checkbox])]:pr-0 print:py-0.5 print:text-xs print:whitespace-normal",
    variant === "muted" && "text-muted-foreground",
    className
  )}
  {...restProps}
>
  {@render children?.()}
</td>
