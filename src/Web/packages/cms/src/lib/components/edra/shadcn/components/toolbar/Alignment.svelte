<script lang="ts">
	import commands from '../../../commands/toolbar-commands.ts';
	import * as DropdownMenu from '@nocturne/ui/ui/dropdown-menu';
	import { Toggle } from '@nocturne/ui/ui/toggle';
	import AlignLeft from '@lucide/svelte/icons/align-left';
	import ChevronDown from '@lucide/svelte/icons/chevron-down';
	import type { Editor } from '@tiptap/core';
	import EdraToolTip from '../EdraToolTip.svelte';
	import strings from '../../../strings.ts';

	interface Props {
		editor: Editor;
	}

	const { editor }: Props = $props();

	const alignments = commands['alignment'];

	const isActive = $derived.by(() => {
		return alignments.find((h) => h.isActive?.(editor)) !== undefined;
	});

	const AlignmentIcon = $derived.by(() => {
		const h = alignments.find((h) => h.isActive?.(editor));
		return h ? h.icon : AlignLeft;
	});
</script>

<DropdownMenu.Root>
	<EdraToolTip tooltip={strings.toolbar.alignment.buttonTitle}>
		{#snippet children({ props }: { props: Record<string, unknown> })}
			<DropdownMenu.Trigger {...props}>
				{#snippet child({ props }: { props: Record<string, unknown> })}
					<Toggle {...props} bind:pressed={() => isActive, () => {}}>
						<span class="flex items-center">
							<AlignmentIcon class="stroke-primary size-4!" />
							<ChevronDown class="text-muted-foreground size-2!" />
						</span>
					</Toggle>
				{/snippet}
			</DropdownMenu.Trigger>
		{/snippet}
	</EdraToolTip>
	<DropdownMenu.Content portalProps={{ to: document.getElementById('edra-editor') ?? 'undefined' }}>
		<DropdownMenu.Label>{strings.toolbar.alignment.dropdownTitle}</DropdownMenu.Label>
		{#each alignments as alignment (alignment)}
			{@const Icon = alignment.icon}
			<DropdownMenu.Item onclick={() => alignment.onClick?.(editor)}>
				<Icon />
				{alignment.tooltip}
				<DropdownMenu.Shortcut>
					{alignment.shortCut}
				</DropdownMenu.Shortcut>
			</DropdownMenu.Item>
		{/each}
	</DropdownMenu.Content>
</DropdownMenu.Root>
