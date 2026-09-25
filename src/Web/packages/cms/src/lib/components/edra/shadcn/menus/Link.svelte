<script lang="ts">
	import SimpleTooltip from '../components/EdraToolTip.svelte';
	import { Button } from '@nocturne/ui/ui/button';
	import { Input } from '@nocturne/ui/ui/input';
	import Check from '@lucide/svelte/icons/check';
	import Copy from '@lucide/svelte/icons/copy';
	import Edit from '@lucide/svelte/icons/edit';
	import Trash from '@lucide/svelte/icons/trash';
	import type { Editor } from '@tiptap/core';
	import BubbleMenu from '../../components/BubbleMenu.svelte';
	import type { ShouldShowProps } from '../../types.ts';
	import strings from '../../strings.ts';

	interface Props {
		editor: Editor;
		parentElement?: HTMLElement;
	}
	const { editor, parentElement }: Props = $props();

	let link = $derived.by(() => editor.getAttributes('link').href);

	let isEditing = $state(false);

	let linkInput = $derived(link);

	function handleSubmit(e: Event) {
		e.preventDefault();
		if (!linkInput || linkInput.trim() === '') return;
		isEditing = false;
		editor.chain().focus().extendMarkRange('link').setLink({ href: linkInput }).run();
	}
</script>

<BubbleMenu
	{editor}
	pluginKey="link-bubble-menu"
	shouldShow={(props: ShouldShowProps) => {
		if (props.editor.isActive('link')) {
			return true;
		} else {
			isEditing = false;
			linkInput = undefined;
			return false;
		}
	}}
	options={{
		shift: true,
		autoPlacement: {
			allowedPlacements: ['top', 'top-end', 'top-start']
		},
		strategy: 'absolute',
		scrollTarget: parentElement
	}}
	class="bg-popover flex h-fit w-fit items-center gap-1 rounded-lg border p-0!"
>
	{#if !isEditing}
		<Button variant="link" size="sm" href={link} class="max-w-120" target="_blank">
			<span class="min-w-0 truncate">{link}</span>
		</Button>
		<SimpleTooltip
			tooltip={strings.menu.link.edit}
			onclick={() => {
				isEditing = true;
				editor.commands.blur();
			}}
		>
			{#snippet children({ props }: { props: Record<string, unknown> })}
				<Button {...props} variant="ghost" size="icon">
					<Edit />
				</Button>
			{/snippet}
		</SimpleTooltip>
		<SimpleTooltip
			tooltip={strings.menu.link.copy}
			onclick={() => {
				window.navigator.clipboard.writeText(link);
			}}
		>
			{#snippet children({ props }: { props: Record<string, unknown> })}
				<Button {...props} variant="ghost" size="icon">
					<Copy />
				</Button>
			{/snippet}
		</SimpleTooltip>
		<SimpleTooltip
			tooltip={strings.menu.link.remove}
			onclick={() => editor.chain().focus().extendMarkRange('link').unsetLink().run()}
		>
			{#snippet children({ props }: { props: Record<string, unknown> })}
				<Button {...props} variant="ghost" size="icon">
					<Trash />
				</Button>
			{/snippet}
		</SimpleTooltip>
	{:else}
		<form onsubmit={handleSubmit} class="flex max-w-120 items-center gap-0.5">
			<Input
				bind:value={linkInput}
				required
				type="url"
				placeholder={strings.menu.link.enterLinkPlaceholder}
			/>
			<SimpleTooltip tooltip={strings.menu.link.enterLinkButton}>
				{#snippet children({ props }: { props: Record<string, unknown> })}
					<Button {...props} type="submit" size="icon">
						<Check />
					</Button>
				{/snippet}
			</SimpleTooltip>
		</form>
	{/if}
</BubbleMenu>
