<script lang="ts" module>
	import { tv, type VariantProps } from "tailwind-variants";

	export const collapsibleVariants = tv({
		base: "",
		variants: {
			variant: {
				default: "",
				// A bordered panel whose trigger row stays visible while collapsed.
				outline: "bg-card rounded-lg border",
			},
		},
		defaultVariants: {
			variant: "default",
		},
	});

	export type CollapsibleVariant = VariantProps<typeof collapsibleVariants>["variant"];
</script>

<script lang="ts">
	import { Collapsible as CollapsiblePrimitive } from "bits-ui";
	import { cn } from "../../../utils";

	let {
		ref = $bindable(null),
		open = $bindable(false),
		class: className,
		variant = "default",
		...restProps
	}: CollapsiblePrimitive.RootProps & { variant?: CollapsibleVariant } = $props();
</script>

<CollapsiblePrimitive.Root
	bind:ref
	bind:open
	data-slot="collapsible"
	class={cn(collapsibleVariants({ variant }), className)}
	{...restProps}
/>
