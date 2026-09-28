<script lang="ts">
	import type { NodeViewProps } from '@tiptap/core';
	import MediaExtended from './MediaExtended.svelte';
	import { safeFrameSrc } from '../../extensions/iframe/safe-src.ts';

	const { ...rest }: NodeViewProps = $props();

	let mediaRef = $state<HTMLElement>();
</script>

<MediaExtended bind:mediaRef {...rest}>
	{@const node = rest.node}
	<iframe bind:this={mediaRef} class="m-0 w-full" {...node.attrs} src={safeFrameSrc(node.attrs.src)}> </iframe>
</MediaExtended>
