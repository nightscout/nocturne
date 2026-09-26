import type { NodeViewProps } from '@tiptap/core';
import type { Component } from 'svelte';
import { SvelteNodeViewRenderer } from 'svelte-tiptap';
import IFrame from './IFrame.ts';
import { safeFrameSrc } from './safe-src.ts';

export const IFrameExtended = (content: Component<NodeViewProps>) =>
	IFrame.extend({
		addAttributes() {
			return {
				src: {
					default: null,
					parseHTML: (element) => safeFrameSrc(element.getAttribute('src')),
					renderHTML: (attributes) => ({ src: safeFrameSrc(attributes.src) })
				},
				alt: {
					default: null
				},
				title: {
					default: null
				},
				width: {
					default: '100%'
				},
				height: {
					default: null
				},
				align: {
					default: 'left'
				}
			};
		},

		addNodeView: () => {
			return SvelteNodeViewRenderer(content);
		}
	});
