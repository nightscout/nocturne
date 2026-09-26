import type { Editor, NodeViewProps } from '@tiptap/core';
import { flushSync, mount, unmount, type Component } from 'svelte';

interface RendererOptions<P extends object> {
	editor: Editor;
	props: P;
}

class SvelteRenderer<R = unknown, P extends object = object> {
	id: string;
	component: Component<{ props: P }>;
	editor: Editor;
	props: P;
	element: HTMLElement;
	ref: R | null = null;
	mnt: ReturnType<typeof mount> | null = null;

	constructor(component: Component<{ props: P }>, { props, editor }: RendererOptions<P>) {
		this.id = Math.floor(Math.random() * 0xffffffff).toString();
		this.component = component;
		this.props = props;
		this.editor = editor;

		this.element = document.createElement('div');
		this.element.classList.add('svelte-renderer');

		if (this.editor.isInitialized) {
			// On first render, we need to flush the render synchronously
			// Renders afterwards can be async, but this fixes a cursor positioning issue
			flushSync(() => {
				this.render();
			});
		} else {
			this.render();
		}
	}

	render(): void {
		this.mnt = mount(this.component, {
			target: this.element,
			props: {
				props: this.props
			}
		});
	}

	updateProps(props: Partial<NodeViewProps>): void {
		Object.assign(this.props, props);
		this.destroy();
		this.render();
	}

	updateAttributes(attributes: Record<string, string>): void {
		Object.keys(attributes).forEach((key) => {
			this.element.setAttribute(key, attributes[key]);
		});
		this.destroy();
		this.render();
	}

	destroy(): void {
		if (this.mnt) {
			unmount(this.mnt);
		}
	}
}

export default SvelteRenderer;
