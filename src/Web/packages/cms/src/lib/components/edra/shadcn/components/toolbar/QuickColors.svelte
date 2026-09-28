<script lang="ts">
	import { quickcolors } from '../../../utils.ts';
	import { Button } from '@nocturne/ui/ui/button';
	import * as Popover from '@nocturne/ui/ui/popover';
	import { Toggle } from '@nocturne/ui/ui/toggle';
	import { cn } from '@nocturne/ui/utils';
	import ChevronDown from '@lucide/svelte/icons/chevron-down';
	import type { Editor } from '@tiptap/core';
	import EdraToolTip from '../EdraToolTip.svelte';
	import strings from '../../../strings.ts';

	interface Props {
		editor: Editor;
	}
	const { editor }: Props = $props();

	const currentColor = $derived.by(() => editor.getAttributes('textStyle').color);
	const currentHighlight = $derived.by(() => editor.getAttributes('highlight').color);

	// The default swatch has no colour of its own, so it keeps a thin outline to stay visible.
	function swatchStyle(value: string, active: boolean, tinted: boolean): string {
		const border = active ? '2px solid' : value === '' ? '1px solid' : '0 solid';
		const text = tinted ? `color: ${value}; font-weight: ${active ? 800 : 400};` : 'font-weight: 400;';
		return `${text} background-color: ${value}50; border: ${border} ${value};`;
	}
</script>

<Popover.Root>
	<EdraToolTip tooltip={strings.toolbar.color.buttonTitle}>
		{#snippet children({ props }: { props: Record<string, unknown> })}
			<Popover.Trigger {...props}>
				{#snippet child({ props }: { props: Record<string, unknown> })}
					<Button {...props} variant="ghost" size="icon">
						<span
							class={cn(
								'flex size-full items-center justify-center gap-0.5 rounded-md',
								currentColor && 'text-(--text-colour)',
								currentHighlight && 'bg-(--highlight-tint)'
							)}
							style:--text-colour={currentColor}
							style:--highlight-tint={currentHighlight && `${currentHighlight}75`}
						>
							<span>{strings.toolbar.color.templateCharacter}</span>
							<ChevronDown class="text-muted-foreground size-2!" />
						</span>
					</Button>
				{/snippet}
			</Popover.Trigger>
		{/snippet}
	</EdraToolTip>
	<Popover.Content class="size-fit" portalProps={{ disabled: true, to: undefined }}>
		<div class="text-muted-foreground my-2 text-xs">{strings.toolbar.color.textColors}</div>
		<!-- Each swatch is painted in the author's content colour, which the theme does not own. -->
		<!-- eslint-disable shadcn/no-inline-styles -->
		<div class="grid grid-cols-5 gap-2">
			{#each quickcolors as color (color)}
				{@const active = editor.isActive('textStyle', { color: color.value })}
				<Toggle
					size="icon-xs"
					bind:pressed={() => active, () => {}}
					style={swatchStyle(color.value, active, true)}
					title={color.label}
					onclick={() => {
						if (color.value === '' || color.label === strings.toolbar.color.default)
							editor.chain().focus().unsetColor().run();
						else
							editor
								.chain()
								.focus()
								.setColor(currentColor === color.value ? '' : color.value)
								.run();
					}}
				>
					{strings.toolbar.color.templateCharacter}
				</Toggle>
			{/each}
		</div>
		<div class="text-muted-foreground my-2 text-xs">{strings.toolbar.color.highlightColors}</div>
		<div class="grid grid-cols-5 gap-2">
			{#each quickcolors as color (color)}
				{@const active = editor.isActive('highlight', { color: color.value })}
				<Toggle
					size="icon-xs"
					bind:pressed={() => active, () => {}}
					style={swatchStyle(color.value, active, false)}
					title={color.label}
					onclick={() => {
						if (color.value === '' || color.label === strings.toolbar.color.default)
							editor.chain().focus().unsetHighlight().run();
						else editor.chain().focus().toggleHighlight({ color: color.value }).run();
					}}
					>{strings.toolbar.color.templateCharacter}
				</Toggle>
			{/each}
		</div>
		<!-- eslint-enable shadcn/no-inline-styles -->
	</Popover.Content>
</Popover.Root>
