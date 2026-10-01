<script lang="ts">
  import { cn } from "../../../utils";
  import { Popover as PopoverPrimitive } from "bits-ui";
  import { triggerVariants, type TriggerVariant } from "../trigger-variants.js";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    ...restProps
  }: PopoverPrimitive.TriggerProps & { variant?: TriggerVariant } = $props();
</script>

<PopoverPrimitive.Trigger
  bind:ref
  data-slot="popover-trigger"
  class={cn(triggerVariants({ variant }), className)}
  {...restProps}
/>
