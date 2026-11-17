<script lang="ts">
	import type { Food } from '#lib/api/index.js';
	import Trash2 from '@lucide/svelte/icons/trash-2';
	import Check from '@lucide/svelte/icons/check';
	import { getFoodState } from './food-context.js';
	import { Button } from '#lib/components/ui/button/index.js';
	import { FoodFields } from '#lib/components/food/index.js';

	interface Props {
		food: Food;
		onsave: (draft: Food) => void;
		oncancel: () => void;
	}

	const { food, onsave, oncancel }: Props = $props();
	const foodState = getFoodState();

	// Intentionally capture initial food value - draft is the local editing copy
	let draft = $state<Food>({ ...food });
	let confirming = $state(false);
	let attributionCount = $state(0);

	async function handleDeleteClick() {
		if (!food._id) return;
		confirming = true;
		attributionCount = await foodState.getAttributionCount(food._id);
	}

	async function confirmDelete() {
		if (!food._id) return;
		await foodState.deleteFood(food._id, 'clear');
	}

	/** Enter saves, after the browser's required-field checks. */
	function handleSubmit(e: SubmitEvent) {
		e.preventDefault();
		onsave(draft);
	}
</script>

<form
	class="border-y border-border bg-background/60 px-4 py-4"
	onsubmit={handleSubmit}
>
	<!-- Delete confirmation bar -->
	{#if confirming}
		<div class="mb-4 flex items-center gap-3 rounded-lg border border-destructive/30 bg-destructive/15 px-4 py-3">
			<Trash2 size={16} class="shrink-0 text-destructive" />
			<span class="text-sm">
				Delete <strong>{food.name}</strong>?
				{#if attributionCount > 0}
					<span class="ml-1 text-muted-foreground">Used in {attributionCount} {'treatment' + (attributionCount === 1 ? '' : 's')}.</span>
				{/if}
			</span>
			<div class="ml-auto flex items-center gap-2">
				<Button variant="ghost" size="sm" onclick={() => { confirming = false; }}>Cancel</Button>
				<Button variant="destructive" size="sm" onclick={confirmDelete}><Trash2 class="h-3 w-3" /> Delete</Button>
			</div>
		</div>
	{/if}

	<FoodFields bind:draft categories={foodState.categoryMap}>
		{#snippet actions()}
			<Button variant="ghost-destructive" size="sm" onclick={handleDeleteClick}><Trash2 class="h-3.5 w-3.5" /> Delete</Button>
			<Button variant="outline" size="sm" onclick={oncancel}>Cancel</Button>
			<Button type="submit" size="sm"><Check class="h-3.5 w-3.5" /> Save changes</Button>
		{/snippet}
	</FoodFields>
</form>
