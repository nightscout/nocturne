import { describe, it, expect } from 'vitest';
import type { JSONContent } from '@tiptap/core';
import { generateHTML, generateJSON } from '@tiptap/html';
import StarterKit from '@tiptap/starter-kit';
import { CalloutExtension } from './callout.ts';

const extensions = [StarterKit, CalloutExtension];

describe('CalloutExtension', () => {
	it('parses a callout div from HTML', () => {
		const html = '<div data-callout="true" data-type="tip"><p>Stay tuned</p></div>';
		const json: JSONContent = generateJSON(html, extensions);
		const calloutNode = json.content?.find((n) => n.type === 'callout');
		expect(calloutNode).toBeDefined();
		expect(calloutNode?.attrs?.type).toBe('tip');
	});

	it('serializes callout back to HTML', () => {
		const doc = {
			type: 'doc',
			content: [
				{
					type: 'callout',
					attrs: { type: 'warning' },
					content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Be careful' }] }]
				}
			]
		};
		const html = generateHTML(doc, extensions);
		expect(html).toContain('data-callout="true"');
		expect(html).toContain('data-type="warning"');
		expect(html).toContain('Be careful');
	});

	it('defaults to info type', () => {
		const html = '<div data-callout="true"><p>Info note</p></div>';
		const json: JSONContent = generateJSON(html, extensions);
		const calloutNode = json.content?.find((n) => n.type === 'callout');
		expect(calloutNode?.attrs?.type).toBe('info');
	});
});
