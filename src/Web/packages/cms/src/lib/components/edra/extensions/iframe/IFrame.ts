import { Node } from '@tiptap/core';
import { safeFrameSrc } from './safe-src.ts';

export interface IframeOptions {
	allowFullscreen: boolean;
	HTMLAttributes: {
		[key: string]: unknown;
	};
}

declare module '@tiptap/core' {
	interface Commands<ReturnType> {
		iframe: {
			/**
			 * Add an iframe with src
			 */
			setIframe: (options: { src: string }) => ReturnType;
			removeIframe: () => ReturnType;
		};
	}
}

export default Node.create<IframeOptions>({
	name: 'iframe',

	group: 'block',

	atom: true,

	addOptions() {
		return {
			allowFullscreen: true,
			HTMLAttributes: {
				class: 'iframe-wrapper'
			}
		};
	},

	addAttributes() {
		return {
			src: {
				default: null,
				parseHTML: (element) => safeFrameSrc(element.getAttribute('src')),
				renderHTML: (attributes) => ({ src: safeFrameSrc(attributes.src) })
			},
			frameborder: {
				default: 0
			},
			allowfullscreen: {
				default: this.options.allowFullscreen,
				parseHTML: () => this.options.allowFullscreen
			}
		};
	},

	parseHTML() {
		return [
			{
				tag: 'iframe'
			}
		];
	},

	renderHTML({ HTMLAttributes }) {
		return ['div', this.options.HTMLAttributes, ['iframe', HTMLAttributes]];
	},

	addCommands() {
		return {
			setIframe:
				(options: { src: string }) =>
				({ tr, dispatch }) => {
					const { selection } = tr;
					const node = this.type.create({ ...options, src: safeFrameSrc(options.src) });

					if (dispatch) {
						tr.replaceRangeWith(selection.from, selection.to, node);
					}

					return true;
				},
			removeIframe:
				() =>
				({ commands }) =>
					commands.deleteNode(this.name)
		};
	}
});
