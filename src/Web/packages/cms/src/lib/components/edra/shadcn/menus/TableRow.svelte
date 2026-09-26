<script lang="ts">
	import { Button } from '@nocturne/ui/ui/button';
	import { Separator } from '@nocturne/ui/ui/separator';
	import ArrowDown from '@lucide/svelte/icons/arrow-down';
	import ArrowDownFromLine from '@lucide/svelte/icons/arrow-down-from-line';
	import ArrowUp from '@lucide/svelte/icons/arrow-up';
	import ArrowUpFromLine from '@lucide/svelte/icons/arrow-up-from-line';
	import Sheet from '@lucide/svelte/icons/sheet';
	import Trash from '@lucide/svelte/icons/trash';
	import { type Editor } from '@tiptap/core';
	import BubbleMenu from '../../components/BubbleMenu.svelte';
	import { isRowGripSelected, moveRowDown, moveRowUp } from '../../extensions/table/utils.ts';
	import type { ShouldShowProps } from '../../types.ts';
	import strings from '../../strings.ts';

	interface Props {
		editor: Editor;
		parentElement?: HTMLElement;
	}

	const { editor, parentElement }: Props = $props();
</script>

<BubbleMenu
	{editor}
	pluginKey="table-row-menu"
	shouldShow={(props: ShouldShowProps) => {
		if (!props.editor.isEditable) return false;
		if (!props.state) {
			return false;
		}
		return isRowGripSelected({ editor, view: props.view, state: props.state, from: props.from });
	}}
	options={{
		shift: {
			crossAxis: true,
			mainAxis: true
		},
		strategy: 'absolute',
		autoPlacement: {
			allowedPlacements: ['bottom', 'top']
		},
		scrollTarget: parentElement
	}}
	class="bg-popover! z-50 flex h-fit w-fit flex-col gap-1 rounded-lg border"
>
	<Button
		variant="menu"
		size="menu"
		title={strings.menu.table.headerRow}
		onclick={() => editor.chain().focus().toggleHeaderRow().run()}
	>
		<Sheet />
		{strings.menu.table.headerRow}
	</Button>
	<Separator />
	<Button
		variant="menu"
		size="menu"
		title={strings.menu.table.addRowAfter}
		onclick={() => editor.chain().focus().addRowAfter().run()}
	>
		<ArrowDownFromLine />
		{strings.menu.table.addRowAfter}
	</Button>
	<Button
		variant="menu"
		size="menu"
		title={strings.menu.table.addRowBefore}
		onclick={() => editor.chain().focus().addRowBefore().run()}
	>
		<ArrowUpFromLine />
		{strings.menu.table.addRowBefore}
	</Button>
	<Separator />
	<Button
		variant="menu"
		size="menu"
		title={strings.menu.table.moveRowUp}
		onclick={() => editor.view.dispatch(moveRowUp(editor.state.tr))}
	>
		<ArrowUp />
		{strings.menu.table.moveRowUp}
	</Button>
	<Button
		variant="menu"
		size="menu"
		title={strings.menu.table.moveRowDown}
		onclick={() => editor.view.dispatch(moveRowDown(editor.state.tr))}
	>
		<ArrowDown />
		{strings.menu.table.moveRowDown}
	</Button>
	<Separator />
	<Button
		variant="ghost-destructive"
		size="menu"
		title={strings.menu.table.deleteRow}
		onclick={() => editor.chain().focus().deleteRow().run()}
	>
		<Trash />
		{strings.menu.table.deleteRow}
	</Button>
</BubbleMenu>
