<script lang="ts">
  import type { Snippet } from "svelte";
  import { Tabs as TabsPrimitive } from "bits-ui";
  import { cn } from "../../../utils";

  let {
    ref = $bindable(null),
    class: className,
    indicator,
    children,
    ...restProps
  }: TabsPrimitive.TriggerProps & {
    /** Decoration drawn inside the trigger, positioned against it; told whether this tab is selected. */
    indicator?: Snippet<[{ active: boolean }]>;
  } = $props();

  const triggerClass = $derived(
    cn(
      "data-[state=active]:bg-background dark:data-[state=active]:text-foreground focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:outline-ring dark:data-[state=active]:border-input dark:data-[state=active]:bg-input/30 text-foreground dark:text-muted-foreground relative inline-flex h-[calc(100%-1px)] flex-1 items-center justify-center gap-1.5 whitespace-nowrap rounded-md border border-transparent px-2 py-1 text-sm font-medium transition-[color,box-shadow] focus-visible:outline-1 focus-visible:ring-[3px] disabled:pointer-events-none disabled:opacity-50 data-[state=active]:shadow-sm [&_svg:not([class*='size-'])]:size-4 [&_svg]:pointer-events-none [&_svg]:shrink-0",
      className
    )
  );
</script>

<!-- Only an indicator takes over rendering, so a caller's own `child` snippet still reaches bits-ui. -->
{#if indicator}
  <TabsPrimitive.Trigger bind:ref data-slot="tabs-trigger" class={triggerClass} {...restProps}>
    {#snippet child({ props })}
      <button {...props}>
        {@render children?.()}
        {@render indicator({ active: props["data-state"] === "active" })}
      </button>
    {/snippet}
  </TabsPrimitive.Trigger>
{:else}
  <TabsPrimitive.Trigger bind:ref data-slot="tabs-trigger" class={triggerClass} {children} {...restProps} />
{/if}
