<script lang="ts">
	import { Tooltip as TooltipPrimitive } from "bits-ui";
	import { cn } from "../../../utils";
	import { triggerVariants, type TriggerVariant } from "../trigger-variants.js";

	let {
		ref = $bindable(null),
		class: className,
		variant = "default",
		...restProps
	}: TooltipPrimitive.TriggerProps & { variant?: TriggerVariant } = $props();
</script>

<TooltipPrimitive.Trigger
	bind:ref
	data-slot="tooltip-trigger"
	class={cn(triggerVariants({ variant }), className)}
	{...restProps}
/>
