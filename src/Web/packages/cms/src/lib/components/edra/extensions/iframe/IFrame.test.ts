import { describe, it, expect } from 'vitest';
import type { JSONContent } from '@tiptap/core';
import { generateHTML, generateJSON } from '@tiptap/html';
import StarterKit from '@tiptap/starter-kit';
import IFrame from './IFrame.ts';

const extensions = [StarterKit, IFrame];

const iframeSrc = (html: string) => {
	const json: JSONContent = generateJSON(html, extensions);
	return json.content?.find((n) => n.type === 'iframe')?.attrs?.src;
};

describe('IFrame', () => {
	it('keeps an https src through parse and render', () => {
		const html = '<iframe src="https://www.youtube.com/embed/abc"></iframe>';
		expect(iframeSrc(html)).toBe('https://www.youtube.com/embed/abc');
		expect(generateHTML(generateJSON(html, extensions), extensions)).toContain(
			'src="https://www.youtube.com/embed/abc"'
		);
	});

	it('drops a javascript: src from pasted HTML', () => {
		expect(iframeSrc('<iframe src="javascript:alert(document.cookie)"></iframe>')).toBeNull();
	});

	it('does not render a javascript: src stored on the node', () => {
		const doc = { type: 'doc', content: [{ type: 'iframe', attrs: { src: 'javascript:alert(1)' } }] };
		expect(generateHTML(doc, extensions)).not.toContain('javascript:');
	});
});
