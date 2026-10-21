import { describe, it, expect } from 'vitest';
import { parse } from 'yaml';
import { generateFrontmatter, toSvx } from './markdown.ts';

function readBack(metadata: Record<string, unknown>): unknown {
	const block = generateFrontmatter(metadata).replace(/^---\n/, '').replace(/\n---$/, '');
	return parse(block);
}

describe('generateFrontmatter', () => {
	it('generates YAML frontmatter from metadata', () => {
		const meta = {
			title: 'Test Post',
			slug: 'test-post',
			date: '2026-04-13',
			tags: ['news', 'update'],
			category: 'announcements',
			author: 'Rhys',
			summary: 'A test post'
		};
		const result = generateFrontmatter(meta);
		expect(result).toContain('---');
		expect(result).toContain('title: Test Post');
		expect(result).toContain('slug: test-post');
		expect(result).toContain('tags: [news, update]');
	});

	it('excludes undefined values', () => {
		const meta = { title: 'Post', slug: 'post', image: undefined };
		const result = generateFrontmatter(meta);
		expect(result).not.toContain('image');
	});

	it('handles boolean values', () => {
		const meta = { title: 'Draft', draft: true };
		const result = generateFrontmatter(meta);
		expect(result).toContain('draft: true');
	});

	it('leaves values that need no quoting unquoted', () => {
		const result = generateFrontmatter({ title: 'Test Post', date: '2026-04-13', tags: [] });
		expect(result).toBe('---\ntitle: Test Post\ndate: 2026-04-13\ntags: []\n---');
	});
});

describe('generateFrontmatter round trip', () => {
	const hostile: Record<string, string> = {
		newline: 'first line\nsecond: line',
		colonSpace: 'Release: the big one',
		hash: 'Issue #12 # not a comment',
		doubleQuote: 'She said "hello"',
		singleQuote: "It's a 'quoted' title",
		backslash: 'C:\\path\\to',
		leadingDash: '- not a list item',
		fence: '---',
		fenceInside: 'before\n---\nafter',
		reserved: 'true',
		numeric: '1.5',
		empty: '',
		trailingSpace: 'ends with space ',
		lineSeparator: `a${String.fromCharCode(0x2028)}b`
	};

	for (const [name, value] of Object.entries(hostile)) {
		it(`reads a ${name} value back unchanged`, () => {
			expect(readBack({ title: value })).toEqual({ title: value });
		});
	}

	it('reads array items back unchanged', () => {
		const tags = ['plain', 'has, comma', 'say "hi"', '---'];
		expect(readBack({ tags })).toEqual({ tags });
	});
});

describe('toSvx', () => {
	it('combines frontmatter and body into a complete .svx file', () => {
		const result = toSvx({ title: 'Post' }, '# Hello\n\nContent here');
		expect(result).toContain('---');
		expect(result).toContain('title: Post');
		expect(result).toContain('# Hello');
	});

	it('includes import block when imports provided', () => {
		const result = toSvx({ title: 'Post' }, '# Hello', [
			"Callout from '@nocturne/cms/components/Callout.svelte'"
		]);
		expect(result).toContain('<script>');
		expect(result).toContain("import Callout from '@nocturne/cms/components/Callout.svelte';");
		expect(result).toContain('</script>');
	});
});
