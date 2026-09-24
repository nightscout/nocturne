<script lang="ts">
	import * as Tooltip from '@nocturne/ui/ui/tooltip';
	import type { Snippet } from 'svelte';

	interface Props {
		tooltip: string;
		children: Snippet<[{ props: Record<string, unknown> }]>;
		shortCut?: string;
		onclick?: (e: MouseEvent) => void;
	}

	const { tooltip, children, shortCut, onclick }: Props = $props();
</script>

<Tooltip.Provider delayDuration={100}>
	<Tooltip.Root>
		<Tooltip.Trigger {onclick}>
			{#snippet child({ props }: { props: Record<string, unknown> })}
				{@render children({ props })}
			{/snippet}
		</Tooltip.Trigger>
		<Tooltip.Content>
			<span>{tooltip}</span>
			{#if shortCut}
				<span class="bg-background text-primary rounded p-0.5">{shortCut}</span>
			{/if}
		</Tooltip.Content>
	</Tooltip.Root>
</Tooltip.Provider>
