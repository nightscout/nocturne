<script lang="ts">
	import * as DropdownMenu from '@nocturne/ui/ui/dropdown-menu';
	import { Toggle } from '@nocturne/ui/ui/toggle';
	import ChevronDown from '@lucide/svelte/icons/chevron-down';
	import Paragraph from '@lucide/svelte/icons/pilcrow';
	import type { Editor } from '@tiptap/core';
	import commands from '../../../commands/toolbar-commands.ts';
	import EdraToolTip from '../EdraToolTip.svelte';
	import strings from '../../../strings.ts';

	interface Props {
		editor: Editor;
	}

	const { editor }: Props = $props();

	const headings = commands['headings'];

	const isActive = $derived.by(() => {
		return headings.find((h) => h.isActive?.(editor)) !== undefined;
	});

	const HeadingIcon = $derived.by(() => {
		const h = headings.find((h) => h.isActive?.(editor));
		return h ? h.icon : Paragraph;
	});
</script>

<DropdownMenu.Root>
	<EdraToolTip tooltip={strings.toolbar.heading.buttonTitle}>
		{#snippet children({ props }: { props: Record<string, unknown> })}
			<DropdownMenu.Trigger {...props}>
				{#snippet child({ props }: { props: Record<string, unknown> })}
					<Toggle {...props} bind:pressed={() => isActive, () => {}}>
						<span class="flex items-center">
							<HeadingIcon class="stroke-primary size-4!" />
							<ChevronDown class="text-muted-foreground size-2!" />
						</span>
					</Toggle>
				{/snippet}
			</DropdownMenu.Trigger>
		{/snippet}
	</EdraToolTip>
	<DropdownMenu.Content portalProps={{ to: document.getElementById('edra-editor') ?? 'undefined' }}>
		<DropdownMenu.Item onclick={() => editor.chain().focus().setParagraph().run()}>
			<Paragraph />
			<span>{strings.command.paragraph}</span>
		</DropdownMenu.Item>
		{#each headings as heading (heading)}
			{@const Icon = heading.icon}
			<DropdownMenu.Item onclick={() => heading.onClick?.(editor)}>
				<Icon />
				{heading.tooltip}
			</DropdownMenu.Item>
		{/each}
	</DropdownMenu.Content>
</DropdownMenu.Root>
