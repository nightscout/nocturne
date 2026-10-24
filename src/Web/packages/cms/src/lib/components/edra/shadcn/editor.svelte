<script lang="ts">
	import { cn } from '@nocturne/ui/utils';
	import { onDestroy, onMount } from 'svelte';
	import initEditor from '../editor.ts';
	import type { EdraEditorProps } from '../types.ts';
	import '../editor.css';
	import './style.css';
	import '../onedark.css';
	import CodeBlockLowlight from '@tiptap/extension-code-block-lowlight';
	import TableOfContents, { getHierarchicalIndexes } from '@tiptap/extension-table-of-contents';
	import { all, createLowlight } from 'lowlight';
	import { toast } from 'svelte-sonner';
	import { SvelteNodeViewRenderer } from 'svelte-tiptap';
	import { AudioExtended } from '../extensions/audio/AudiExtended.ts';
	import { AudioPlaceholder } from '../extensions/audio/AudioPlaceholder.ts';
	import { FileDrop } from '../extensions/HandleFileDrop.ts';
	import { IFrameExtended } from '../extensions/iframe/IFrameExtended.ts';
	import { IFramePlaceholder } from '../extensions/iframe/IFramePlaceholder.ts';
	import { ImageExtended } from '../extensions/image/ImageExtended.ts';
	import { ImagePlaceholder } from '../extensions/image/ImagePlaceholder.ts';
	import slashcommand from '../extensions/slash-command/slashcommand.ts';
	import { VideoExtended } from '../extensions/video/VideoExtended.ts';
	import { VideoPlaceholder } from '../extensions/video/VideoPlaceholder.ts';
	import { getHandleDropImage, getHandlePasteImage } from '../utils.ts';
	import AudioExtendedComp from './components/AudioExtended.svelte';
	import AudioPlaceHolderComp from './components/AudioPlaceHolder.svelte';
	import CodeBlock from './components/CodeBlock.svelte';
	import IFrameExtendedComp from './components/IFrameExtended.svelte';
	import IFramePlaceHolderComp from './components/IFramePlaceHolder.svelte';
	import ImageExtendedComp from './components/ImageExtended.svelte';
	import ImagePlaceholderComp from './components/ImagePlaceholder.svelte';
	import SlashCommandList from './components/SlashCommandList.svelte';
	import VideoExtendedComp from './components/VideoExtended.svelte';
	import VideoPlaceHolderComp from './components/VideoPlaceholder.svelte';
	import Link from './menus/Link.svelte';
	import TableCol from './menus/TableCol.svelte';
	import TableRow from './menus/TableRow.svelte';

	const lowlight = createLowlight(all);

	let {
		editor = $bindable(),
		editable = true,
		content,
		element = $bindable<HTMLElement>(),
		onUpdate,
		autofocus = false,
		class: className,
		spellcheck = true,
		onFileSelect,
		onDropOrPaste,
		additionalExtensions = [],
	}: EdraEditorProps = $props();

	onMount(() => {
		editor = initEditor(
			element,
			content,
			[
				CodeBlockLowlight.configure({
					lowlight
				}).extend({
					addNodeView() {
						return SvelteNodeViewRenderer(CodeBlock);
					}
				}),
				ImagePlaceholder(ImagePlaceholderComp),
				ImageExtended(ImageExtendedComp),
				VideoPlaceholder(VideoPlaceHolderComp),
				VideoExtended(VideoExtendedComp, onDropOrPaste),
				AudioPlaceholder(AudioPlaceHolderComp),
				AudioExtended(AudioExtendedComp, onDropOrPaste),
				IFramePlaceholder(IFramePlaceHolderComp),
				IFrameExtended(IFrameExtendedComp),
				slashcommand(SlashCommandList),
				FileDrop.configure({
					handler: onFileSelect
				}),
				TableOfContents.configure({
					getIndex: getHierarchicalIndexes,
					scrollParent: () => element || window
				}),
				...additionalExtensions,
			],
			{
				onUpdate,
				onTransaction(props) {
					editor = undefined;
					editor = props.editor;
				},
				onContentError: (error) => {
					toast.error('Could not load the content', {
						description: 'The content of this page might be corrupted.'
					});
					console.error(error);
				},
				editable,
				autofocus
			}
		);
		editor.setOptions({
			editorProps: {
				handlePaste: getHandlePasteImage(onDropOrPaste),
				handleDrop: getHandleDropImage(onDropOrPaste)
			}
		});
	});

	onDestroy(() => {
		if (editor) editor.destroy();
	});
</script>

{#if editor && !editor.isDestroyed}
	<Link {editor} parentElement={element} />
	<TableCol {editor} parentElement={element} />
	<TableRow {editor} parentElement={element} />
{/if}

<div
	bind:this={element}
	id="edra-editor"
	class={cn('edra-editor h-full w-full cursor-auto *:outline-none', className)}
	{spellcheck}
></div>
