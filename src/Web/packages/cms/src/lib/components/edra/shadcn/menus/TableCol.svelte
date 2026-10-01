<script lang="ts">
	import { Button } from '@nocturne/ui/ui/button';
	import { Separator } from '@nocturne/ui/ui/separator';
	import ArrowLeft from '@lucide/svelte/icons/arrow-left';
	import ArrowLeftFromLine from '@lucide/svelte/icons/arrow-left-from-line';
	import ArrowRight from '@lucide/svelte/icons/arrow-right';
	import ArrowRightFromLine from '@lucide/svelte/icons/arrow-right-from-line';
	import Sheet from '@lucide/svelte/icons/sheet';
	import Trash from '@lucide/svelte/icons/trash';
	import { type Editor } from '@tiptap/core';
	import BubbleMenu from '../../components/BubbleMenu.svelte';
	import {
		isColumnGripSelected,
		moveColumnLeft,
		moveColumnRight
	} from '../../extensions/table/utils.ts';
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
	pluginKey="table-col-menu"
	shouldShow={(props: ShouldShowProps) => {
		if (!props.editor.isEditable) return false;
		if (!props.state) {
			return false;
		}
		return isColumnGripSelected({ editor, view: props.view, state: props.state, from: props.from });
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
		title={strings.menu.table.headerColumn}
		onclick={() => editor.chain().focus().toggleHeaderColumn().run()}
	>
		<Sheet />
		{strings.menu.table.headerColumn}
	</Button>
	<Separator />
	<Button
		variant="menu"
		size="menu"
		title={strings.menu.table.addColumnAfter}
		onclick={() => editor.chain().focus().addColumnAfter().run()}
	>
		<ArrowRightFromLine />
		{strings.menu.table.addColumnAfter}
	</Button>
	<Button
		variant="menu"
		size="menu"
		title={strings.menu.table.addColumnBefore}
		onclick={() => editor.chain().focus().addColumnBefore().run()}
	>
		<ArrowLeftFromLine />
		{strings.menu.table.addColumnBefore}
	</Button>
	<Separator />
	<Button
		variant="menu"
		size="menu"
		title={strings.menu.table.moveColumnLeft}
		onclick={() => editor.view.dispatch(moveColumnLeft(editor.state.tr))}
	>
		<ArrowLeft />
		{strings.menu.table.moveColumnLeft}
	</Button>
	<Button
		variant="menu"
		size="menu"
		title={strings.menu.table.moveColumnRight}
		onclick={() => editor.view.dispatch(moveColumnRight(editor.state.tr))}
	>
		<ArrowRight />
		{strings.menu.table.moveColumnRight}
	</Button>
	<Separator />
	<Button
		variant="ghost-destructive"
		size="menu"
		title={strings.menu.table.deleteColumn}
		onclick={() => editor.chain().focus().deleteColumn().run()}
	>
		<Trash />
		{strings.menu.table.deleteColumn}
	</Button>
</BubbleMenu>
