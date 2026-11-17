<script lang="ts" module>
  export type TabsListVariant = "tabs" | "wrap" | "tiles";

  const bar = "bg-muted text-muted-foreground rounded-lg p-[3px]";

  const variantClass: Record<TabsListVariant, string> = {
    tabs: `${bar} inline-flex w-fit items-center`,
    wrap: `${bar} grid w-full grid-cols-[repeat(auto-fit,minmax(7.5rem,1fr))] gap-y-0.5`,
    tiles: "grid w-full grid-cols-[repeat(auto-fit,minmax(4.5rem,1fr))] gap-2",
  };
</script>

<script lang="ts">
  import { Tabs as TabsPrimitive } from "bits-ui";
  import { cn } from "../../../utils";

  let {
    ref = $bindable(null),
    class: className,
    variant = "tabs",
    ...restProps
  }: TabsPrimitive.ListProps & {
    /**
     * "tabs" is one row at the width of its triggers. "wrap" fills its container and wraps triggers that do not fit
     * into rows of equal width; "tiles" does the same with separate tiles, each an icon over its label.
     * An auto-fit grid needs a definite width, so only the filling variants can wrap.
     */
    variant?: TabsListVariant;
  } = $props();
</script>

<TabsPrimitive.List
  bind:ref
  data-slot="tabs-list"
  data-variant={variant}
  class={cn("group/tabs-list justify-center", variantClass[variant], className)}
  {...restProps}
/>
