<script lang="ts">
	import { distinct } from "$lib/utils/collections";
	import type { Food } from '$api';
	import { Plus, X, ChevronRight } from 'lucide-svelte';
	import GiIcon from './GiIcon.svelte';
	import { getFoodState } from './food-context.js';
	import { giFromInt, giToInt, isGiLevel } from './types.js';
	import type { GiLevel } from './types.js';
	import { FOOD_UNITS, DEFAULT_PORTION, DEFAULT_GI } from '$lib/components/food';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Label } from '$lib/components/ui/label';
	import * as ToggleGroup from '$lib/components/ui/toggle-group';
	import * as Collapsible from '$lib/components/ui/collapsible';

	interface Props {
		onadd: (food: Food) => Promise<void> | void;
		onclose: () => void;
	}

	const { onadd, onclose }: Props = $props();
	const foodState = getFoodState();

	const giLevels: GiLevel[] = ['low', 'medium', 'high'];

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
	let showDetails = $state(false);
	let nameInput: HTMLInputElement | undefined = $state();

	let saving = $state(false);
	const canSave = $derived(
		!saving && !!draft.name && draft.carbs !== undefined && !!draft.portion
	);

	const subcategories = $derived.by(() => {
		if (!draft.category) return [];
		return distinct(
			foodState.foods
				.filter((f) => f.category === draft.category)
				.map((f) => f.subcategory || null)
		).sort();
	});

	$effect(() => {
		nameInput?.focus();
	});

	async function submit(addAnother: boolean) {
		// The add is awaited and the buttons disabled meanwhile: a second click
		// through an in-flight create would otherwise add the food twice.
		if (!canSave) return;
		saving = true;
		try {
			await onadd(draft);
		} finally {
			saving = false;
		}
		if (addAnother) {
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
		if (mod && e.key === 'Enter') {
			e.preventDefault();
			submit(true);
		} else if (e.key === 'Escape') {
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
		<div class="flex items-center justify-center rounded-[7px] size-6.5 bg-carbs/12">
			<Plus size={14} class="text-carbs" />
		</div>
		<span class="font-semibold text-sm">Add food</span>
		<span class="text-muted-foreground text-xs">
			Tab through fields · Enter to save · ⌘+Enter to save and add another · Esc to close
		</span>
		<Button type="button" variant="ghost" size="icon-sm" class="ml-auto" onclick={onclose}><X class="h-3.5 w-3.5" /></Button>
	</div>

	<!-- Single-row form -->
	<div class="grid h-[42px] grid-cols-[1.6fr_110px_90px_1fr_1.4fr] items-end gap-3">
		<!-- Name -->
		<div class="flex h-full flex-col gap-1">
			<label for="composer-name" class="text-muted-foreground font-medium uppercase text-2xs">Name</label>
			<div class="flex flex-1 items-center rounded-md px-3 border border-input dark:bg-input/30">
				<input
					id="composer-name"
					name="name"
					type="text"
					required
					class="w-full bg-transparent text-sm outline-none"
					placeholder="e.g. Greek yogurt, plain"
					bind:this={nameInput}
					bind:value={draft.name}
				/>
			</div>
		</div>

		<!-- Carbs -->
		<div class="flex h-full flex-col gap-1">
			<label for="composer-carbs" class="font-medium uppercase text-2xs text-carbs">Carbs</label>
			<div class="flex flex-1 items-center rounded-md px-3 border border-carbs/45 bg-carbs/6">
				<input
					id="composer-carbs"
					name="carbs"
					type="number"
					required
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.carbs}
					min="0"
					step="0.1"
				/>
				<span class="ml-1 shrink-0 text-xs text-carbs">g</span>
			</div>
		</div>

		<!-- Per (portion) -->
		<div class="flex h-full flex-col gap-1">
			<label for="composer-portion" class="text-muted-foreground font-medium uppercase text-2xs">Per</label>
			<div class="flex flex-1 items-center rounded-md px-3 border border-input dark:bg-input/30">
				<input
					id="composer-portion"
					name="portion"
					type="number"
					required
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.portion}
					min="0"
					step="1"
				/>
			</div>
		</div>

		<!-- Unit -->
		<div class="flex h-full flex-col gap-1">
			<span id="composer-unit-label" class="text-muted-foreground font-medium uppercase text-2xs">Unit</span>
			<ToggleGroup.Root aria-labelledby="composer-unit-label" type="single" value={draft.unit ?? 'g'} onValueChange={(v: string) => { if (v) draft = { ...draft, unit: v }; }} variant="outline" size="sm" class="w-full flex-1">
				{#each FOOD_UNITS as u (u)}
					<ToggleGroup.Item value={u} class="flex-1">{u}</ToggleGroup.Item>
				{/each}
			</ToggleGroup.Root>
		</div>

		<!-- GI -->
		<div class="flex h-full flex-col gap-1">
			<span id="composer-gi-label" class="text-muted-foreground font-medium uppercase text-2xs">GI</span>
			<ToggleGroup.Root aria-labelledby="composer-gi-label" type="single" value={giFromInt(draft.gi)} onValueChange={(v: string) => { if (isGiLevel(v)) draft = { ...draft, gi: giToInt(v) }; }} variant="outline" size="sm" class="w-full flex-1">
				{#each giLevels as g (g)}
					<ToggleGroup.Item value={g} class="capitalize">
						<GiIcon level={g} size={7} />{g}
					</ToggleGroup.Item>
				{/each}
			</ToggleGroup.Root>
		</div>
	</div>

	<!-- Footer -->
	<div class="mt-3 flex items-center justify-between">
		<!-- Details toggle -->
		<Collapsible.Root bind:open={showDetails}>
			<Collapsible.Trigger>
				{#snippet child({ props }: { props: Record<string, unknown> })}
					<Button {...props} data-testid="food-composer-details" variant="subtle" size="inline-xs" class="select-none">
						<span class="inline-flex transition-transform" class:rotate-90={showDetails}><ChevronRight class="h-3 w-3" /></span> {showDetails ? 'Hide' : 'Add'} fat, protein, category...
					</Button>
				{/snippet}
			</Collapsible.Trigger>
			<Collapsible.Content>
			<div class="mt-3 grid grid-cols-5 gap-3">
				<!-- Fat -->
				<div class="flex flex-col gap-1">
					<Label for="composer-fat" size="sm" variant="muted">Fat</Label>
					<div class="flex items-center gap-2">
						<Input
							type="number"
							id="composer-fat"
							name="fat"
							bind:value={draft.fat}
							min="0"
							step="0.1"
						/>
						<span class="shrink-0 text-xs text-muted-foreground">g</span>
					</div>
				</div>

				<!-- Protein -->
				<div class="flex flex-col gap-1">
					<Label for="composer-protein" size="sm" variant="muted">Protein</Label>
					<div class="flex items-center gap-2">
						<Input
							type="number"
							id="composer-protein"
							name="protein"
							bind:value={draft.protein}
							min="0"
							step="0.1"
						/>
						<span class="shrink-0 text-xs text-muted-foreground">g</span>
					</div>
				</div>

				<!-- Energy -->
				<div class="flex flex-col gap-1">
					<Label for="composer-energy" size="sm" variant="muted">Energy</Label>
					<div class="flex items-center gap-2">
						<Input
							type="number"
							id="composer-energy"
							name="energy"
							bind:value={draft.energy}
							min="0"
							step="1"
						/>
						<span class="shrink-0 text-xs text-muted-foreground">kcal</span>
					</div>
				</div>

				<!-- Category -->
				<div class="flex flex-col gap-1">
					<Label for="composer-category" size="sm" variant="muted">Category</Label>
					<Select.Root type="single" name="category" value={draft.category ?? ''} onValueChange={(v) => { draft = { ...draft, category: v }; }}>
						<Select.Trigger id="composer-category" class="w-full">
							{draft.category || 'Category'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="None" />
							{#each foodState.categories as cat (cat)}
								<Select.Item value={cat} label={cat} />
							{/each}
						</Select.Content>
					</Select.Root>
				</div>

				<!-- Subcategory -->
				<div class="flex flex-col gap-1">
					<Label for="composer-subcategory" size="sm" variant="muted">Subcategory</Label>
					<Select.Root type="single" name="subcategory" value={draft.subcategory ?? ''} onValueChange={(v) => { draft = { ...draft, subcategory: v }; }}>
						<Select.Trigger id="composer-subcategory" class="w-full">
							{draft.subcategory || 'Subcategory'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="None" />
							{#each subcategories as sub (sub)}
								<Select.Item value={sub} label={sub} />
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
			</div>
			</Collapsible.Content>
		</Collapsible.Root>

		<!-- Action buttons -->
		<div class="flex items-center gap-2">
			<Button type="submit" variant="outline" size="sm" disabled={!canSave}>Save</Button>
			<Button type="button" size="sm" disabled={!canSave} onclick={() => submit(true)}>Save & add another <span class="ml-1 text-xs opacity-60">⌘+Enter</span></Button>
		</div>
	</div>
</form>
