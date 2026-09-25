<script lang="ts">
	import { Button } from '@nocturne/ui/ui/button';
	import * as DropdownMenu from '@nocturne/ui/ui/dropdown-menu';
	import ChevronDown from '@lucide/svelte/icons/chevron-down';
	import { Editor } from '@tiptap/core';
	import EdraToolTip from '../EdraToolTip.svelte';
	import strings from '../../../strings.ts';

	interface Props {
		editor: Editor;
	}

	const { editor }: Props = $props();

	const FONT_SIZE = [
		{ label: strings.toolbar.font.tiny, value: '0.7rem' },
		{ label: strings.toolbar.font.smaller, value: '0.75rem' },
		{ label: strings.toolbar.font.small, value: '0.9rem' },
		{ label: strings.toolbar.font.default, value: '' },
		{ label: strings.toolbar.font.large, value: '1.25rem' },
		{ label: strings.toolbar.font.extraLarge, value: '1.5rem' }
	];

	let currentSize = $derived.by(() => editor.getAttributes('textStyle').fontSize || '');

	const currentLabel = $derived.by(() => {
		const l = FONT_SIZE.find((f) => f.value === currentSize);
		if (l) return l.label.split(' ')[0];
		return 'Medium';
	});
</script>

<DropdownMenu.Root>
	<EdraToolTip tooltip={strings.toolbar.font.buttonTitle}>
		{#snippet children({ props }: { props: Record<string, unknown> })}
			<DropdownMenu.Trigger {...props}>
				{#snippet child({ props }: { props: Record<string, unknown> })}
					<Button {...props} variant="ghost" size="sm">
						<span class="flex items-center">
							{currentLabel}
							<ChevronDown class="text-muted-foreground size-2!" />
						</span>
					</Button>
				{/snippet}
			</DropdownMenu.Trigger>
		{/snippet}
	</EdraToolTip>
	<DropdownMenu.Content portalProps={{ to: document.getElementById('edra-editor') ?? 'undefined' }}>
		<DropdownMenu.Label>{strings.toolbar.font.dropdownTitle}</DropdownMenu.Label>
		{#each FONT_SIZE as fontSize (fontSize)}
			<DropdownMenu.Item
				onclick={() => {
					editor.chain().focus().setFontSize(fontSize.value).run();
				}}
				>{fontSize.label}
				<DropdownMenu.Shortcut>
					{fontSize.value}
				</DropdownMenu.Shortcut>
			</DropdownMenu.Item>
		{/each}
	</DropdownMenu.Content>
</DropdownMenu.Root>
