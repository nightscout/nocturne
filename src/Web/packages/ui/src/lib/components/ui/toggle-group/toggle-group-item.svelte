<script lang="ts">
  import { ToggleGroup as ToggleGroupPrimitive } from "bits-ui";
  import { getToggleGroupCtx, type ToggleGroupCtx } from "./toggle-group.svelte";
  import { cn } from "../../../utils";
  import { toggleVariants } from "../toggle/index.js";

  let {
    ref = $bindable(null),
    value = $bindable(),
    class: className,
    size,
    variant,
    ...restProps
  }: ToggleGroupPrimitive.ItemProps & Omit<ToggleGroupCtx, "spacing"> = $props();

  const ctx = getToggleGroupCtx();
  const resolvedVariant = $derived(ctx.variant || variant);
  const resolvedSize = $derived(ctx.size || size);
  const segmented = $derived(resolvedVariant === "segmented");
  const toggleVariant = $derived(resolvedVariant === "segmented" ? "default" : resolvedVariant);
  const joined = $derived(!segmented && !ctx.spacing);
</script>

<ToggleGroupPrimitive.Item
  bind:ref
  data-slot="toggle-group-item"
  data-variant={resolvedVariant}
  data-size={resolvedSize}
  data-spacing={ctx.spacing ?? 0}
  class={cn(
    toggleVariants({
      variant: toggleVariant,
      size: resolvedSize,
    }),
    "min-w-0 flex-1 shrink-0 shadow-none focus:z-10 focus-visible:z-10",
    joined &&
      "rounded-none first:rounded-l-md last:rounded-r-md data-[variant=outline]:border-l-0 data-[variant=outline]:first:border-l",
    segmented &&
      "text-muted-foreground hover:text-foreground hover:bg-transparent data-[state=on]:bg-background data-[state=on]:text-foreground data-[state=on]:shadow-xs",
    className
  )}
  {value}
  {...restProps}
/>
