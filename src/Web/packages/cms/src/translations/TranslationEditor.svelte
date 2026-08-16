<script lang="ts">
	import { Button } from '@nocturne/ui/ui/button';
	import { Input } from '@nocturne/ui/ui/input';
	import { Progress } from '@nocturne/ui/ui/progress';
	import * as Select from '@nocturne/ui/ui/select';
	import type { TranslationMessage } from './po';
	import TranslationRow from './TranslationRow.svelte';

	type Filter = 'all' | 'untranslated' | 'drafts';

	interface Props {
		messages: TranslationMessage[];
		/** Draft values keyed by messageKey(context, msgid). */
		drafts: Map<string, string[]>;
		ondraft: (message: TranslationMessage, values: string[] | null) => void;
	}

	let { messages, drafts, ondraft }: Props = $props();

	let search = $state('');
	let filter = $state<Filter>('all');
	let page = $state(0);
	const pageSize = 50;

	// Keys drafted since the last filter/page/search change. The
	// 'untranslated' filter keeps these visible so a row does not vanish
	// from under the user on their first keystroke.
	let touched = $state(new Set<string>());

	function onRowDraft(message: TranslationMessage, values: string[] | null) {
		if (!touched.has(message.key)) {
			touched = new Set([...touched, message.key]);
		}
		ondraft(message, values);
	}

	const filterLabels: Record<Filter, string> = {
		all: 'All messages',
		untranslated: 'Untranslated',
		drafts: 'With drafts',
	};

	const filtered = $derived.by(() => {
		const needle = search.trim().toLowerCase();
		return messages.filter((m) => {
			if (
				filter === 'untranslated' &&
				(m.upstream.some((v) => v.length > 0) || (drafts.has(m.key) && !touched.has(m.key)))
			)
				return false;
			if (filter === 'drafts' && !drafts.has(m.key)) return false;
			if (needle.length === 0) return true;
			return (
				m.msgid.toLowerCase().includes(needle) ||
				m.context.toLowerCase().includes(needle) ||
				m.upstream.some((v) => v.toLowerCase().includes(needle)) ||
				(drafts.get(m.key)?.some((v) => v.toLowerCase().includes(needle)) ?? false)
			);
		});
	});

	const pageCount = $derived(Math.max(1, Math.ceil(filtered.length / pageSize)));
	const clampedPage = $derived(Math.min(page, pageCount - 1));
	const visible = $derived(filtered.slice(clampedPage * pageSize, (clampedPage + 1) * pageSize));

	const translatedCount = $derived(
		messages.filter((m) => m.upstream.some((v) => v.length > 0) || drafts.has(m.key)).length,
	);
</script>

<div class="space-y-4">
	<div class="space-y-1">
		<div class="flex items-center justify-between text-sm">
			<span>{translatedCount} of {messages.length} messages translated or drafted</span>
			<span class="text-muted-foreground">{drafts.size} draft{drafts.size === 1 ? '' : 's'}</span>
		</div>
		<Progress value={messages.length === 0 ? 0 : (translatedCount / messages.length) * 100} />
	</div>

	<div class="flex flex-wrap items-center gap-2">
		<Input
			value={search}
			oninput={(e: Event) => {
				search = (e.currentTarget as HTMLInputElement).value;
				page = 0;
				touched = new Set();
			}}
			placeholder="Search source text or translations"
			class="max-w-sm"
		/>
		<Select.Root
			type="single"
			value={filter}
			onValueChange={(v: string | undefined) => {
				filter = (v as Filter) ?? 'all';
				page = 0;
				touched = new Set();
			}}
		>
			<Select.Trigger class="w-44">{filterLabels[filter]}</Select.Trigger>
			<Select.Content>
				<Select.Item value="all">{filterLabels.all}</Select.Item>
				<Select.Item value="untranslated">{filterLabels.untranslated}</Select.Item>
				<Select.Item value="drafts">{filterLabels.drafts}</Select.Item>
			</Select.Content>
		</Select.Root>
	</div>

	{#if visible.length === 0}
		<p class="py-8 text-center text-sm text-muted-foreground">No messages match.</p>
	{:else}
		<div class="space-y-3">
			{#each visible as message (message.key)}
				<TranslationRow
					{message}
					draft={drafts.get(message.key)}
					ondraft={(values) => onRowDraft(message, values)}
				/>
			{/each}
		</div>
	{/if}

	{#if pageCount > 1}
		<div class="flex items-center justify-center gap-3 text-sm">
			<Button
				variant="outline"
				size="sm"
				disabled={clampedPage === 0}
				onclick={() => { page = clampedPage - 1; touched = new Set(); }}
			>
				Previous
			</Button>
			<span>Page {clampedPage + 1} of {pageCount}</span>
			<Button
				variant="outline"
				size="sm"
				disabled={clampedPage >= pageCount - 1}
				onclick={() => { page = clampedPage + 1; touched = new Set(); }}
			>
				Next
			</Button>
		</div>
	{/if}
</div>
