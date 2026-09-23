<script lang="ts">
  import type { ComponentProps } from "svelte";
  import { cn } from "../../../utils";
  import { Input } from "../input/index.js";

  let {
    ref = $bindable(null),
    value = $bindable(),
    class: className,
    ...restProps
  }: ComponentProps<typeof Input> = $props();
</script>

<Input
  bind:ref
  bind:value
  data-slot="input-group-control"
  class={cn(
    "flex-1 rounded-none border-0 bg-transparent shadow-none focus-visible:ring-0 dark:bg-transparent",
    className
  )}
  {...restProps}
/>
