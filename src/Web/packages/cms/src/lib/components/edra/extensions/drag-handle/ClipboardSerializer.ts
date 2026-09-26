import type { Slice } from '@tiptap/pm/model';
import type { EditorView } from '@tiptap/pm/view';

export function serializeForClipboard(view: EditorView, slice: Slice) {
	return view.serializeForClipboard(slice);
}
