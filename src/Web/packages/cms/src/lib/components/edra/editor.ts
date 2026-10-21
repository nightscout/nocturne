import { type Content, Editor, type EditorOptions, type Extensions } from '@tiptap/core';
import Highlight from '@tiptap/extension-highlight';
import { TaskItem, TaskList } from '@tiptap/extension-list';
import Subscript from '@tiptap/extension-subscript';
import Superscript from '@tiptap/extension-superscript';
import TextAlign from '@tiptap/extension-text-align';
import { Color, FontSize, TextStyle } from '@tiptap/extension-text-style';
import Typography from '@tiptap/extension-typography';
import { CharacterCount, Placeholder } from '@tiptap/extensions';
import StarterKit from '@tiptap/starter-kit';
import AutoJoiner from 'tiptap-extension-auto-joiner';
import { ColorHighlighter } from './extensions/ColorHighlighter.ts';
import { Table, TableCell, TableHeader, TableRow } from './extensions/table/index.ts';
import { Markdown } from '@tiptap/markdown';
import strings from './strings.ts';

export default (
	element?: HTMLElement,
	content?: Content,
	extensions?: Extensions,
	options?: Partial<EditorOptions>
) => {
	const editor = new Editor({
		element,
		content,
		extensions: [
			StarterKit.configure({
				orderedList: {
					HTMLAttributes: {
						class: 'list-decimal'
					}
				},
				bulletList: {
					HTMLAttributes: {
						class: 'list-disc'
					}
				},
				heading: {
					levels: [1, 2, 3, 4]
				},
				link: {
					openOnClick: false,
					autolink: true,
					linkOnPaste: true,
					HTMLAttributes: {
						target: '_tab',
						rel: 'noopener noreferrer nofollow'
					}
				},
				codeBlock: false
			}),
			CharacterCount,
			Highlight.configure({
				multicolor: true
			}),
			Placeholder.configure({
				emptyEditorClass: 'is-empty',
				// Use a placeholder:
				// Use different placeholders depending on the node type:
				placeholder: ({ node }) => {
					if (node.type.name === 'heading') {
						return strings.editor.headingPlaceholder;
					}
					if (node.type.name === 'paragraph') {
						return strings.editor.paragraphPlaceholder;
					}
					return '';
				}
			}),
			Color,
			Subscript,
			Superscript,
			Typography,
			ColorHighlighter,
			TextStyle,
			FontSize,
			TextAlign.configure({
				types: ['heading', 'paragraph']
			}),
			TaskList,
			TaskItem.configure({
				nested: true
			}),
			AutoJoiner,
			Table,
			TableHeader,
			TableRow,
			TableCell,
			Markdown,

			...(extensions ?? [])
		],
		...options
	});

	return editor;
};
