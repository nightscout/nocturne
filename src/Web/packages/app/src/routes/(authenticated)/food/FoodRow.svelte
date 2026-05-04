<script lang="ts">
	import type { Food } from '$api';
	import { Star, ChevronRight } from 'lucide-svelte';
	import CarbPill from './CarbPill.svelte';
	import GiChip from './GiChip.svelte';
	import { giFromInt } from './types';
	import { Toggle } from '$lib/components/ui/toggle';

	interface Props {
		food: Food;
		expanded: boolean;
		favorite: boolean;
		ontoggle: () => void;
		onfavorite: () => void;
	}

	const { food, expanded, favorite, ontoggle, onfavorite }: Props = $props();
</script>

<div
	class="food-row"
	class:expanded
	role="button"
	tabindex="0"
	onclick={ontoggle}
	onkeydown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); ontoggle(); } }}
>
	<span class="star-cell">
		<Toggle
			pressed={favorite}
			onPressedChange={() => onfavorite()}
			variant="default"
			size="sm"
			class="h-6 w-6 p-0"
			aria-label={favorite ? 'Remove from favorites' : 'Add to favorites'}
			onclick={(e) => e.stopPropagation()}
		>
			<Star class="h-3.5 w-3.5" fill={favorite ? 'currentColor' : 'none'} style={favorite ? 'color: var(--carbs-strong)' : undefined} />
		</Toggle>
	</span>

	<span class="name-cell">
		<span class="name-text">{food.name ?? ''}</span>
		{#if food.category || food.subcategory}
			<span class="meta-line">
				{#if food.category}
					<span class="meta-category">{food.category}</span>
				{/if}
				{#if food.category && food.subcategory}
					<span class="meta-dot">&middot;</span>
				{/if}
				{#if food.subcategory}
					<span>{food.subcategory}</span>
				{/if}
			</span>
		{/if}
	</span>

	<span class="carb-cell">
		<CarbPill value={food.carbs ?? 0} />
	</span>

	<span class="portion-cell">
		per {food.portion ?? 0} {food.unit ?? 'g'}
	</span>

	<span class="gi-cell">
		<GiChip level={giFromInt(food.gi)} />
	</span>

	<span class="energy-cell">
		{food.energy ?? 0} kcal
	</span>

	<span class="chevron-cell">
		<ChevronRight size={14} />
	</span>
</div>

<style>
	.food-row {
		display: grid;
		grid-template-columns: 24px 1fr 110px 130px 90px 70px 24px;
		gap: 12px;
		padding: 10px 16px;
		align-items: center;
		cursor: pointer;
		border: none;
		border-bottom: 1px solid var(--border);
		background: transparent;
		width: 100%;
		text-align: left;
		font: inherit;
		color: inherit;
		transition: background-color 150ms ease;
	}

	.food-row:hover {
		background: oklch(1 0 0 / 0.025);
	}

	.food-row.expanded {
		background: oklch(1 0 0 / 0.04);
	}

	.star-cell {
		display: flex;
		align-items: center;
		justify-content: center;
	}

	.name-cell {
		display: flex;
		flex-direction: column;
		gap: 2px;
		min-width: 0;
	}

	.name-text {
		font-size: 14px;
		font-weight: 500;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	.meta-line {
		display: flex;
		align-items: center;
		gap: 4px;
		font-size: 12px;
		color: var(--muted-foreground);
	}

	.meta-category {
		display: flex;
		align-items: center;
		gap: 4px;
	}

	.meta-dot {
		opacity: 0.5;
	}

	.carb-cell {
		display: flex;
		align-items: center;
	}

	.portion-cell {
		font-size: 13px;
		color: var(--muted-foreground);
		font-variant-numeric: tabular-nums;
		white-space: nowrap;
	}

	.gi-cell {
		display: flex;
		align-items: center;
	}

	.energy-cell {
		font-size: 12px;
		color: var(--muted-foreground);
		font-variant-numeric: tabular-nums;
		text-align: right;
		white-space: nowrap;
	}

	.chevron-cell {
		display: flex;
		align-items: center;
		justify-content: center;
		color: var(--muted-foreground);
	}
</style>
