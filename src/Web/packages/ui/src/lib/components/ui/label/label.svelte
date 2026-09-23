<script lang="ts">
  import { Label as LabelPrimitive } from "bits-ui";
  import { cn } from "../../../utils";
  import {
    labelVariants,
    type LabelSize,
    type LabelVariant,
  } from "./index.js";

  let {
    ref = $bindable(null),
    class: className,
    size = "default",
    variant = "default",
    ...restProps
  }: LabelPrimitive.RootProps & {
    size?: LabelSize;
    variant?: LabelVariant;
  } = $props();
</script>

<LabelPrimitive.Root
  bind:ref
  data-slot="label"
  class={cn(labelVariants({ size, variant }), className)}
  {...restProps}
/>
