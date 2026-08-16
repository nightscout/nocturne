<script lang="ts">
	import { Badge } from '@nocturne/ui/ui/badge';
	import { Button } from '@nocturne/ui/ui/button';
	import { Textarea } from '@nocturne/ui/ui/textarea';
	import { RotateCcw } from '@lucide/svelte';
	import type { TranslationMessage } from './po';

	interface Props {
		message: TranslationMessage;
		/** Current draft values, or undefined when no draft exists. */
		draft: string[] | undefined;
		/** null clears the draft; values must have message.forms entries. */
		ondraft: (values: string[] | null) => void;
	}

	let { message, draft, ondraft }: Props = $props();

	const values = $derived(
		draft ?? Array.from({ length: message.forms }, (_, n) => message.upstream[n] ?? ''),
	);

	function update(index: number, value: string) {
		const next = [...values];
		next[index] = value;
		// Only a value identical to upstream clears the draft. An emptied
		// field must stay a draft, or the textarea would snap back to the
		// upstream text mid-edit.
		const matchesUpstream = next.every((v, n) => v === (message.upstream[n] ?? ''));
		ondraft(matchesUpstream ? null : next);
	}

	const hasUpstream = $derived(message.upstream.some((v) => v.length > 0));
</script>

<div class="rounded-lg border border-border/60 p-4 space-y-3">
	<div class="flex items-start justify-between gap-3">
		<div class="min-w-0 space-y-1">
			{#if message.context}
				<Badge variant="outline">{message.context}</Badge>
			{/if}
			<p class="font-medium whitespace-pre-wrap break-words">{message.msgid}</p>
			{#if message.msgidPlural}
				<p class="text-sm text-muted-foreground whitespace-pre-wrap break-words">
					Plural: {message.msgidPlural}
				</p>
			{/if}
		</div>
		<div class="flex items-center gap-2 shrink-0">
			{#if draft}
				<Badge>Draft</Badge>
				<Button
					variant="ghost"
					size="sm"
					onclick={() => ondraft(null)}
					aria-label="Discard draft"
				>
					<RotateCcw class="h-4 w-4" />
				</Button>
			{:else if message.fuzzy}
				<Badge variant="secondary">Fuzzy</Badge>
			{:else if hasUpstream}
				<Badge variant="secondary">Translated</Badge>
			{:else}
				<Badge variant="outline">Untranslated</Badge>
			{/if}
		</div>
	</div>

	{#each { length: message.forms } as _, n (n)}
		<div class="space-y-1">
			{#if message.forms > 1}
				<p class="text-xs text-muted-foreground">Form {n}</p>
			{/if}
			<Textarea
				value={values[n] ?? ''}
				oninput={(e: Event) => update(n, (e.currentTarget as HTMLTextAreaElement).value)}
				placeholder={message.upstream[n]?.length ? '' : 'No translation yet'}
				rows={Math.min(4, Math.max(1, Math.ceil(message.msgid.length / 80)))}
				class="font-normal"
			/>
		</div>
	{/each}
</div>
