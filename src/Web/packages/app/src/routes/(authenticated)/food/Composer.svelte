<script lang="ts">
	import { Key } from '@nocturne/ui/keys';
	import type { Food } from '#lib/api/index.js';
	import Plus from '@lucide/svelte/icons/plus';
	import X from '@lucide/svelte/icons/x';
	import { DEFAULT_PORTION, DEFAULT_GI, FoodFields } from '#lib/components/food/index.js';
	import { Button } from '#lib/components/ui/button/index.js';
	import { PaintedMoment, SubmitButton, toastSaved } from '#lib/forms/index.js';
	import { getFoodState } from './food-context.js';

	interface Props {
		/** Resolves to the created food, or null when the create failed. */
		onadd: (food: Food) => Promise<Food | null>;
		onclose: () => void;
	}

	const { onadd, onclose }: Props = $props();
	const foodState = getFoodState();

	function emptyDraft(): Food {
		return {
			name: undefined,
			carbs: undefined,
			portion: DEFAULT_PORTION,
			unit: 'g',
			gi: DEFAULT_GI,
			type: 'food',
			fat: undefined,
			protein: undefined,
			energy: undefined,
			category: undefined,
			subcategory: undefined,
		};
	}

	let draft = $state<Food>(emptyDraft());
	let nameInput: HTMLInputElement | null = $state(null);

	let saving = $state(false);
	/** Foods added through "Save & add another"; the composer stays open to show them land. */
	let addedAnother = $state(0);
	const canSave = $derived(
		!saving && !!draft.name && draft.carbs !== undefined && !!draft.portion
	);

	$effect(() => {
		nameInput?.focus();
	});

	async function submit(addAnother: boolean) {
		// The add is awaited and the buttons disabled meanwhile: a second click
		// through an in-flight create would otherwise add the food twice.
		if (!canSave) return;
		saving = true;
		let created: Food | null;
		try {
			created = await onadd(draft);
		} finally {
			saving = false;
		}
		if (!created) return;
		if (addAnother) {
			addedAnother++;
			const keepPortion = draft.portion;
			const keepUnit = draft.unit;
			const keepGi = draft.gi;
			const keepCategory = draft.category;
			draft = emptyDraft();
			draft.portion = keepPortion;
			draft.unit = keepUnit;
			draft.gi = keepGi;
			draft.category = keepCategory;
			nameInput?.focus();
		} else {
			toastSaved('Food created');
			onclose();
		}
	}

	/** Enter saves and closes, after the browser's required-field checks. */
	function handleSubmit(e: SubmitEvent) {
		e.preventDefault();
		submit(false);
	}

	function handleKeydown(e: KeyboardEvent) {
		const mod = e.metaKey || e.ctrlKey;
		if (mod && e.key === Key.Enter) {
			e.preventDefault();
			submit(true);
		} else if (e.key === Key.Escape) {
			e.preventDefault();
			onclose();
		}
	}
</script>

<!-- The keydown handler carries the composer's own shortcuts (save and add
     another, close) — submit and required-field checks come from the form. -->
<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<form
	data-testid="food-composer"
	class="mx-4 my-3 rounded-lg border border-carbs/22 bg-carbs/4 p-3.5"
	onsubmit={handleSubmit}
	onkeydown={handleKeydown}
>
	<!-- Header -->
	<div class="mb-3 flex items-center gap-3">
		<div class="flex items-center justify-center rounded-md size-6.5 bg-carbs/12">
			<Plus size={14} class="text-entry-carbs" />
		</div>
		<span class="font-semibold text-sm">Add food</span>
		<span class="text-muted-foreground text-xs">
			Tab through fields · Enter to save · ⌘+Enter to save and add another · Esc to close
		</span>
		<Button type="button" variant="ghost" size="icon-sm" class="ml-auto" onclick={onclose}><X class="h-3.5 w-3.5" /></Button>
	</div>

	<FoodFields bind:draft categories={foodState.categoryMap} bind:nameRef={nameInput} collapseDetails>
		{#snippet actions()}
			<div class="flex items-center gap-2">
				<PaintedMoment count={addedAnother} artwork="apple" class="text-xs text-muted-foreground">Added</PaintedMoment>
				<Button type="submit" variant="outline" size="sm" disabled={!canSave}>Save</Button>
				<SubmitButton type="button" size="sm" saved={addedAnother} disabled={!canSave} onclick={() => submit(true)}>Save & add another <span class="ml-1 text-xs opacity-60">⌘+Enter</span></SubmitButton>
			</div>
		{/snippet}
	</FoodFields>
</form>
