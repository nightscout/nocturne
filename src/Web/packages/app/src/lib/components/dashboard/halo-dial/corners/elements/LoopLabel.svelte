<script lang="ts">
	import { Activity } from "lucide-svelte";

	interface Props {
		value: { status: string } | null;
	}

	const { value }: Props = $props();

	const text = $derived(value === null ? "--" : value.status);
</script>

<span
	class="inline-flex items-center gap-1 text-[10px] text-foreground"
	style="font-family: var(--font-mono);"
	data-testid="loop-label"
>
	<Activity
		class={value === null ? "size-3 opacity-40" : "size-3"}
		aria-hidden="true"
	/>
	<span>{text}</span>
</span>
