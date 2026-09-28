<script lang="ts">
	import { Accordion as AccordionPrimitive } from "bits-ui";

	let {
		ref = $bindable(null),
		value = $bindable(),
		...restProps
	}: AccordionPrimitive.RootProps = $props();
</script>

<!-- eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- bits-ui types value as a union keyed by `type`, which destructuring for $bindable splits apart -->
<AccordionPrimitive.Root bind:value={value as never}
	bind:ref
	data-slot="accordion"
	{...restProps}
/>
