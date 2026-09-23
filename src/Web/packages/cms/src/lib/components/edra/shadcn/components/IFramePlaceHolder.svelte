<script lang="ts">
	import type { NodeViewProps } from '@tiptap/core';

	const { editor }: NodeViewProps = $props();

	import { Button, buttonVariants } from '@nocturne/ui/ui/button';
	import { Input } from '@nocturne/ui/ui/input';
	import * as Popover from '@nocturne/ui/ui/popover';
	import CodeXml from '@lucide/svelte/icons/code-xml';
	import { NodeViewWrapper } from 'svelte-tiptap';
	import strings from '../../strings.ts';

	let open = $state(false);
	let iframUrl = $state('');

	function handleSubmit(e: Event) {
		e.preventDefault();
		open = false;
		editor.chain().focus().setIframe({ src: iframUrl }).run();
	}
</script>

<NodeViewWrapper
	as="div"
	contenteditable="false"
	class={buttonVariants({
		variant: 'secondary',
		class: 'relative my-4! select-none w-full justify-start p-6'
	})}
	draggable={true}
	onclick={() => (open = true)}
>
	<CodeXml />
	<span>{strings.extension.iframe.insertPlaceholder}</span>
	<Popover.Root bind:open>
		<Popover.Trigger class="sr-only absolute left-1/2"
			>{strings.extension.iframe.openButton}</Popover.Trigger
		>
		<Popover.Content
			onCloseAutoFocus={(e) => e.preventDefault()}
			contenteditable={false}
			class="bg-popover w-96 p-4 transition-all duration-300"
			portalProps={{ disabled: true, to: undefined }}
		>
			<form onsubmit={handleSubmit} class="flex flex-col gap-2">
				<Input
					placeholder={strings.extension.iframe.embedLinkPlaceholder}
					bind:value={iframUrl}
					required
					type="url"
				/>
				<Button type="submit" variant="secondary">{strings.extension.iframe.embedLinkButton}</Button
				>
			</form>
		</Popover.Content>
	</Popover.Root>
</NodeViewWrapper>
