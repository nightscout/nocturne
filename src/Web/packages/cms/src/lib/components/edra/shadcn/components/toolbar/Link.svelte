<script lang="ts">
	import { Button } from '@nocturne/ui/ui/button';
	import { Input } from '@nocturne/ui/ui/input';
	import * as Popover from '@nocturne/ui/ui/popover';
	import { Toggle } from '@nocturne/ui/ui/toggle';
	import Check from '@lucide/svelte/icons/check';
	import ChevronDown from '@lucide/svelte/icons/chevron-down';
	import Link from '@lucide/svelte/icons/link';
	import type { Editor } from '@tiptap/core';
	import EdraToolTip from '../EdraToolTip.svelte';
	import strings from '../../../strings.ts';

	interface Props {
		editor: Editor;
		open?: boolean;
	}

	let { editor, open = $bindable(false) }: Props = $props();

	let value = $state<string>();

	function handleSubmit(e: Event) {
		e.preventDefault();
		if (value === undefined || value.trim() === '') return;
		editor.chain().focus().setLink({ href: value }).run();
		value = undefined;
		open = false;
	}
</script>

<Popover.Root bind:open>
	<EdraToolTip tooltip={strings.toolbar.link.buttonTitle}>
		{#snippet children({ props }: { props: Record<string, unknown> })}
			<Popover.Trigger {...props}>
				{#snippet child({ props }: { props: Record<string, unknown> })}
					<Toggle {...props} bind:pressed={() => editor.isActive('link'), () => {}}>
						<span class="flex items-center">
							<Link />
							<ChevronDown class="text-muted-foreground size-2!" />
						</span>
					</Toggle>
				{/snippet}
			</Popover.Trigger>
		{/snippet}
	</EdraToolTip>
	<Popover.Content
		portalProps={{ to: document.getElementById('edra-editor') ?? undefined }}
		class="h-fit w-80 p-0"
	>
		<form class="flex items-center gap-0.5" onsubmit={handleSubmit}>
			<Input
				placeholder={strings.toolbar.link.insertLinkPlaceholder}
				bind:value
				required
				type="url"
			/>
			<EdraToolTip tooltip={strings.toolbar.link.insertLink}>
				{#snippet children({ props }: { props: Record<string, unknown> })}
					<Button {...props} type="submit" size="icon">
						<Check />
					</Button>
				{/snippet}
			</EdraToolTip>
		</form>
	</Popover.Content>
</Popover.Root>
