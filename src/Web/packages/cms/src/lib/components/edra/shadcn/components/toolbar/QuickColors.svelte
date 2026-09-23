<script lang="ts">
	import { quickcolors } from '../../../utils.ts';
	import { Button, buttonVariants } from '@nocturne/ui/ui/button';
	import * as Popover from '@nocturne/ui/ui/popover';
	import { cn } from '@nocturne/ui/utils';
	import ChevronDown from '@lucide/svelte/icons/chevron-down';
	import type { Editor } from '@tiptap/core';
	import EdraToolTip from '../EdraToolTip.svelte';
	import strings from '../../../strings.ts';

	interface Props {
		class?: string;
		editor: Editor;
	}
	const { class: className = '', editor }: Props = $props();

	const currentColor = $derived.by(() => editor.getAttributes('textStyle').color);
	const currentHighlight = $derived.by(() => editor.getAttributes('highlight').color);
</script>

<Popover.Root>
	<Popover.Trigger>
		<EdraToolTip tooltip={strings.toolbar.color.buttonTitle}>
			<div
				class={buttonVariants({
					variant: 'ghost',
					size: 'icon',
					class: cn(
						'gap-0.5',
						currentColor && 'text-(--text-colour)!',
						currentHighlight && 'bg-(--highlight-tint)!',
						className
					)
				})}
				style:--text-colour={currentColor}
				style:--highlight-tint={currentHighlight && `${currentHighlight}75`}
			>
				<span>{strings.toolbar.color.templateCharacter}</span>
				<ChevronDown class="text-muted-foreground size-2!" />
			</div>
		</EdraToolTip>
	</Popover.Trigger>
	<Popover.Content class="size-fit shadow-lg" portalProps={{ disabled: true, to: undefined }}>
		<div class="text-muted-foreground my-2 text-xs">{strings.toolbar.color.textColors}</div>
		<!-- Each swatch is painted in the author's content colour, which the theme does not own. -->
		<!-- eslint-disable shadcn/no-inline-styles -->
		<div class="grid grid-cols-5 gap-2">
			{#each quickcolors as color (color)}
				<Button
					variant="ghost"
					class={cn(
						`size-6 border-0 p-0 font-normal`,
						editor.isActive('textStyle', { color: color.value }) && 'border-2 font-extrabold',
						color.value === '' && 'border'
					)}
					style={`color: ${color.value}; background-color: ${color.value}50; border-color: ${color.value};`}
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
				</Button>
			{/each}
		</div>
		<div class="text-muted-foreground my-2 text-xs">{strings.toolbar.color.highlightColors}</div>
		<div class="grid grid-cols-5 gap-2">
			{#each quickcolors as color (color)}
				<Button
					variant="ghost"
					class={cn(
						`size-6 border-0 p-0 font-normal`,
						editor.isActive('highlight', { color: color.value }) && 'border-2',
						color.value === '' && 'border'
					)}
					style={`background-color: ${color.value}50; border-color: ${color.value};`}
					title={color.label}
					onclick={() => {
						if (color.value === '' || color.label === strings.toolbar.color.default)
							editor.chain().focus().unsetHighlight().run();
						else editor.chain().focus().toggleHighlight({ color: color.value }).run();
					}}
					>{strings.toolbar.color.templateCharacter}
				</Button>
			{/each}
		</div>
		<!-- eslint-enable shadcn/no-inline-styles -->
	</Popover.Content>
</Popover.Root>
