<script lang="ts">
	import { Button } from '@nocturne/ui/ui/button';
	import { Toggle } from '@nocturne/ui/ui/toggle';
	import type { Editor } from '@tiptap/core';
	import type { EdraToolBarCommands } from '../../commands/types.ts';
	import EdraToolTip from './EdraToolTip.svelte';

	interface Props {
		editor: Editor;
		command: EdraToolBarCommands;
	}

	const { editor, command }: Props = $props();

	const disabled = $derived(command.clickable ? !command.clickable(editor) : false);
</script>

<EdraToolTip
	tooltip={command.tooltip ?? ''}
	shortCut={command.shortCut ?? ''}
	onclick={() => command.onClick?.(editor)}
>
	{#snippet children({ props }: { props: Record<string, unknown> })}
		{@const Icon = command.icon}
		{#if command.isActive}
			<!-- The editor owns the state: a click runs the command and the next transaction reports it back. -->
			<Toggle
				{...props}
				bind:pressed={() => command.isActive?.(editor) ?? false, () => {}}
				{disabled}
			>
				<Icon />
			</Toggle>
		{:else}
			<Button {...props} variant="ghost" size="icon" {disabled}>
				<Icon />
			</Button>
		{/if}
	{/snippet}
</EdraToolTip>
