<script lang="ts">
	import commands from '../../../commands/toolbar-commands.ts';
	import * as DropdownMenu from '@nocturne/ui/ui/dropdown-menu';
	import { Toggle } from '@nocturne/ui/ui/toggle';
	import ChevronDown from '@lucide/svelte/icons/chevron-down';
	import Minus from '@lucide/svelte/icons/minus';
	import type { Editor } from '@tiptap/core';
	import EdraToolTip from '../EdraToolTip.svelte';
	import strings from '../../../strings.ts';

	interface Props {
		editor: Editor;
	}

	const { editor }: Props = $props();

	const lists = commands['lists'];

	const isActive = $derived.by(() => {
		return lists.find((h) => h.isActive?.(editor)) !== undefined;
	});

	const ListIcon = $derived.by(() => {
		const h = lists.find((h) => h.isActive?.(editor));
		return h ? h.icon : Minus;
	});
</script>

<DropdownMenu.Root>
	<EdraToolTip tooltip={strings.toolbar.list.buttonTitle}>
		{#snippet children({ props }: { props: Record<string, unknown> })}
			<DropdownMenu.Trigger {...props}>
				{#snippet child({ props }: { props: Record<string, unknown> })}
					<Toggle {...props} bind:pressed={() => isActive, () => {}}>
						<span class="flex items-center">
							<ListIcon class="stroke-primary size-4!" />
							<ChevronDown class="text-muted-foreground size-2!" />
						</span>
					</Toggle>
				{/snippet}
			</DropdownMenu.Trigger>
		{/snippet}
	</EdraToolTip>
	<DropdownMenu.Content portalProps={{ to: document.getElementById('edra-editor') ?? 'undefined' }}>
		<DropdownMenu.Label>{strings.toolbar.list.dropdownTitle}</DropdownMenu.Label>
		{#each lists as list (list)}
			{@const Icon = list.icon}
			<DropdownMenu.Item onclick={() => list.onClick?.(editor)}>
				<Icon />
				{list.tooltip}
				<DropdownMenu.Shortcut>{list.shortCut}</DropdownMenu.Shortcut>
			</DropdownMenu.Item>
		{/each}
	</DropdownMenu.Content>
</DropdownMenu.Root>
