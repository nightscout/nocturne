import { Node, mergeAttributes } from '@tiptap/core';

export type CalloutType = 'info' | 'warning' | 'danger' | 'tip';

declare module '@tiptap/core' {
	interface Commands<ReturnType> {
		callout: {
			setCallout: (attrs?: { type?: CalloutType }) => ReturnType;
			toggleCallout: (attrs?: { type?: CalloutType }) => ReturnType;
		};
	}
}

export const CalloutExtension = Node.create({
	name: 'callout',
	group: 'block',
	content: 'block+',

	addAttributes() {
		return {
			type: {
				default: 'info' satisfies CalloutType,
				parseHTML: (element: HTMLElement) => element.getAttribute('data-type') || 'info',
				renderHTML: (attributes: Record<string, unknown>) => ({
					'data-type': attributes.type
				})
			}
		};
	},

	parseHTML() {
		return [
			{
				tag: 'div[data-callout]'
			}
		];
	},

	renderHTML({ HTMLAttributes }) {
		return ['div', mergeAttributes(HTMLAttributes, { 'data-callout': 'true' }), 0];
	},

	addCommands() {
		return {
			setCallout:
				(attrs?: { type?: CalloutType }) =>
				({ commands }) => {
					return commands.wrapIn(this.name, attrs);
				},
			toggleCallout:
				(attrs?: { type?: CalloutType }) =>
				({ commands }) => {
					return commands.toggleWrap(this.name, attrs);
				}
		};
	}
});
