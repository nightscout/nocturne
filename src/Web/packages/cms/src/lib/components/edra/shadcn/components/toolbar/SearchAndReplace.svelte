<script lang="ts">
	import { Button } from '@nocturne/ui/ui/button';
	import { Input } from '@nocturne/ui/ui/input';
	import * as Popover from '@nocturne/ui/ui/popover';
	import { Toggle } from '@nocturne/ui/ui/toggle';
	import { cn } from '@nocturne/ui/utils';
	import ArrowLeft from '@lucide/svelte/icons/arrow-left';
	import ArrowRight from '@lucide/svelte/icons/arrow-right';
	import CaseSensitive from '@lucide/svelte/icons/case-sensitive';
	import ChevronRight from '@lucide/svelte/icons/chevron-right';
	import Replace from '@lucide/svelte/icons/replace';
	import ReplaceAll from '@lucide/svelte/icons/replace-all';
	import Search from '@lucide/svelte/icons/search';
	import type { Editor } from '@tiptap/core';
	import { slide } from 'svelte/transition';
	import EdraToolTip from '../EdraToolTip.svelte';
	import { getKeyboardShortcut } from '../../../utils.ts';
	import strings from '../../../strings.ts';

	interface Props {
		editor: Editor;
	}

	const { editor }: Props = $props();

	let open = $state(false);
	let showMore = $state(false);

	let searchText = $state('');
	let replaceText = $state('');
	let caseSensitive = $state(false);

	let searchIndex = $derived(editor.storage?.searchAndReplace?.resultIndex);
	let searchCount = $derived(editor.storage?.searchAndReplace?.results.length);

	function updateSearchTerm(clearIndex = false) {
		if (clearIndex) editor.commands.resetIndex();

		editor.commands.setSearchTerm(searchText);
		editor.commands.setReplaceTerm(replaceText);
		editor.commands.setCaseSensitive(caseSensitive);
	}

	function goToSelection() {
		const { results, resultIndex } = editor.storage.searchAndReplace;
		const position = results[resultIndex];
		if (!position) return;
		editor.commands.setTextSelection(position);
		const { node } = editor.view.domAtPos(editor.state.selection.anchor);
		if (node instanceof HTMLElement) node.scrollIntoView({ behavior: 'smooth', block: 'center' });
	}

	function replace() {
		editor.commands.replace();
		goToSelection();
	}

	const next = () => {
		editor.commands.nextSearchResult();
		goToSelection();
	};

	const previous = () => {
		editor.commands.previousSearchResult();
		goToSelection();
	};

	const clear = () => {
		searchText = '';
		replaceText = '';
		caseSensitive = false;
	};

	const replaceAll = () => editor.commands.replaceAll();

	function handleKeyDown(e: KeyboardEvent) {
		if (e.key === 'Escape' && open) {
			e.preventDefault();
			open = false;
		} else if ((e.metaKey || e.ctrlKey) && e.key === 'f') {
			e.preventDefault();
			open = true;
		}
	}
</script>

<svelte:document onkeydown={handleKeyDown} />

<Popover.Root
	bind:open
	onOpenChange={(value) => {
		if (value === false) {
			clear();
			updateSearchTerm();
		}
	}}
>
	<EdraToolTip
		tooltip={strings.toolbar.searchAndReplace.buttonTitle}
		shortCut={getKeyboardShortcut('F', true)}
	>
		{#snippet children({ props }: { props: Record<string, unknown> })}
			<Popover.Trigger {...props}>
				{#snippet child({ props }: { props: Record<string, unknown> })}
					<Button {...props} variant="ghost" size="icon">
						<Search />
					</Button>
				{/snippet}
			</Popover.Trigger>
		{/snippet}
	</EdraToolTip>
	<Popover.Content
		class="flex w-fit items-center gap-1 p-2"
		portalProps={{ disabled: true, to: undefined }}
	>
		<Toggle
			size="icon-xs"
			bind:pressed={showMore}
			title={strings.toolbar.searchAndReplace.showMore}
		>
			<ChevronRight class={cn('size-4 transition-transform', showMore && 'rotate-90')} />
		</Toggle>
		<div class="flex size-full flex-col gap-1">
			<div class="flex w-full items-center gap-1">
				<Input
					placeholder={strings.toolbar.searchAndReplace.searchPlaceholder}
					bind:value={searchText}
					oninput={() => updateSearchTerm()}
					class="w-48"
				/>
				<span class="text-muted-foreground text-sm"
					>{searchCount > 0 ? searchIndex + 1 : 0}/{searchCount}
				</span>
				<EdraToolTip tooltip={strings.toolbar.searchAndReplace.caseSensitive}>
					{#snippet children({ props }: { props: Record<string, unknown> })}
						<Toggle
							{...props}
							size="icon-xs"
							bind:pressed={caseSensitive}
							onPressedChange={(pressed) => {
								caseSensitive = pressed;
								updateSearchTerm();
							}}
						>
							<CaseSensitive class="size-4" />
						</Toggle>
					{/snippet}
				</EdraToolTip>
				<EdraToolTip tooltip={strings.toolbar.searchAndReplace.goToPrevious} onclick={previous}>
					{#snippet children({ props }: { props: Record<string, unknown> })}
						<Button
							{...props}
							variant="ghost"
							size="icon"
							class="size-7"
						>
							<ArrowLeft />
						</Button>
					{/snippet}
				</EdraToolTip>
				<EdraToolTip tooltip={strings.toolbar.searchAndReplace.goToNext} onclick={next}>
					{#snippet children({ props }: { props: Record<string, unknown> })}
						<Button
							{...props}
							variant="ghost"
							size="icon"
							class="size-7"
						>
							<ArrowRight />
						</Button>
					{/snippet}
				</EdraToolTip>
			</div>
			{#if showMore}
				<div transition:slide class="flex w-full items-center gap-1">
					<Input
						placeholder={strings.toolbar.searchAndReplace.replacePlaceholder}
						bind:value={replaceText}
						oninput={() => updateSearchTerm()}
						class="w-48"
					/>
					<EdraToolTip tooltip={strings.toolbar.searchAndReplace.replace} onclick={replace}>
						{#snippet children({ props }: { props: Record<string, unknown> })}
							<Button {...props} variant="ghost" size="icon" class="size-7">
								<Replace />
							</Button>
						{/snippet}
					</EdraToolTip>
					<EdraToolTip tooltip={strings.toolbar.searchAndReplace.replaceAll} onclick={replaceAll}>
						{#snippet children({ props }: { props: Record<string, unknown> })}
							<Button {...props} variant="ghost" size="icon" class="size-7">
								<ReplaceAll />
							</Button>
						{/snippet}
					</EdraToolTip>
				</div>
			{/if}
		</div>
	</Popover.Content>
</Popover.Root>
