<script lang="ts" module>
  import { getContext, setContext } from "svelte";
  import type { ToggleSize, ToggleVariant } from "../toggle/index.js";

  /**
   * `segmented` sits the items in a muted track and raises the selected one,
   * the same look as a TabsList, for switching between views of one thing.
   */
  export type ToggleGroupVariant = ToggleVariant | "segmented";

  /** 0 joins the items into one bar; 1 separates them into individual chips. */
  export type ToggleGroupSpacing = 0 | 1;

  export type ToggleGroupCtx = {
    variant?: ToggleGroupVariant;
    size?: ToggleSize;
    spacing?: ToggleGroupSpacing;
  };

  export function setToggleGroupCtx(props: ToggleGroupCtx) {
    setContext("toggleGroup", props);
  }

  export function getToggleGroupCtx() {
    return getContext<ToggleGroupCtx>("toggleGroup");
  }
</script>

<script lang="ts">
  import { ToggleGroup as ToggleGroupPrimitive } from "bits-ui";
  import { cn } from "../../../utils";

  let {
    ref = $bindable(null),
    value = $bindable(),
    class: className,
    size = "default",
    variant = "default",
    spacing = 0,
    ...restProps
  }: ToggleGroupPrimitive.RootProps & ToggleGroupCtx = $props();

  // svelte-ignore state_referenced_locally
  setToggleGroupCtx({
    variant,
    size,
    spacing,
  });
</script>

<!--
Discriminated Unions + Destructing (required for bindable) do not
get along, so we shut typescript up by casting `value` to `never`.
-->
<ToggleGroupPrimitive.Root
  bind:value={value as never}
  bind:ref
  data-slot="toggle-group"
  data-variant={variant}
  data-size={size}
  data-spacing={spacing}
  class={cn(
    "group/toggle-group data-[variant=outline]:shadow-xs flex w-fit items-center rounded-md",
    "data-[spacing=1]:gap-1 data-[spacing=1]:data-[variant=outline]:shadow-none",
    "data-[variant=segmented]:bg-muted data-[variant=segmented]:rounded-lg data-[variant=segmented]:p-0.5",
    className
  )}
  {...restProps}
/>
