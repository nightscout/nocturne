<script lang="ts">
	import { getDirectionInfo } from "$lib/utils";

	interface Props {
		value: { direction: string } | null;
	}

	const { value }: Props = $props();

	const info = $derived(value === null ? null : getDirectionInfo(value.direction));
</script>

<span
	class="inline-flex items-center gap-1 text-[10px] text-foreground"
	style="font-family: var(--font-mono);"
	data-testid="direction"
	data-direction={value?.direction ?? "none"}
>
	{#if info}
		{@const Icon = info.icon}
		<Icon class={`size-3 ${info.css}`} aria-hidden="true" />
		<span>{info.label}</span>
	{:else}
		<span>--</span>
	{/if}
</span>
