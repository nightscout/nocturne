<script lang="ts" module>
  import type { Snippet } from "svelte";

  export interface ConnectorData {
    type: string;
    displayName: string;
    description: string;
    category: string;
    fields?: unknown[];
  }
</script>

<script lang="ts">
  import { ToggleGroup as ToggleGroupPrimitive } from "bits-ui";
  import { cn } from "$lib/utils";

  let {
    ref = $bindable(null),
    value = $bindable<string[]>([]),
    class: className,
    onValueChange,
    children,
    ...restProps
  }: Omit<
    ToggleGroupPrimitive.RootProps,
    "type" | "value" | "onValueChange" | "children"
  > & {
    value?: string[];
    onValueChange?: (value: string[]) => void;
    children?: Snippet;
  } = $props();

  function handleValueChange(newValue: string[]) {
    value = newValue;
    onValueChange?.(newValue);
  }
</script>

<ToggleGroupPrimitive.Root
  bind:ref
  value={value as never}
  onValueChange={handleValueChange}
  type="multiple"
  data-slot="connector-toggle-group"
  class={cn("flex flex-col gap-3", className)}
  {...restProps}
>
  {@render children?.()}
</ToggleGroupPrimitive.Root>
