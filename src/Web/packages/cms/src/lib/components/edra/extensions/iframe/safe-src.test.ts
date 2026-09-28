import { describe, it, expect } from 'vitest';
import { safeFrameSrc } from './safe-src.ts';

describe('safeFrameSrc', () => {
	it('keeps http, https and relative URLs unchanged', () => {
		expect(safeFrameSrc('https://www.youtube.com/embed/abc')).toBe('https://www.youtube.com/embed/abc');
		expect(safeFrameSrc('http://example.com')).toBe('http://example.com');
		expect(safeFrameSrc('/embed/page')).toBe('/embed/page');
		expect(safeFrameSrc('example.com/embed')).toBe('example.com/embed');
	});

	it('drops script and inline-content schemes, however they are spelled', () => {
		for (const src of [
			'javascript:alert(1)',
			'JavaScript:alert(1)',
			'  javascript:alert(1)',
			'java\tscript:alert(1)',
			'\u0001javascript:alert(1)',
			'data:text/html,<script>alert(1)</script>',
			'blob:https://example.com/uuid',
			'vbscript:msgbox(1)'
		]) {
			expect(safeFrameSrc(src), src).toBeNull();
		}
	});

	it('drops non-strings', () => {
		expect(safeFrameSrc(null)).toBeNull();
		expect(safeFrameSrc(undefined)).toBeNull();
		expect(safeFrameSrc(42)).toBeNull();
	});
});
