import { describe, it, expect } from 'vitest';
import { serializeComponentToSvx } from './svelte-component-svx.ts';

const props = (value: Record<string, string>) => JSON.stringify(value);

describe('serializeComponentToSvx', () => {
	it('writes plain values as quoted attributes and "true" as a bare flag', () => {
		expect(serializeComponentToSvx('LanguageSelector', props({ compact: 'true', label: 'Pick one' }))).toBe(
			'<LanguageSelector compact label="Pick one" />'
		);
	});

	it('writes a value that could leave its attribute as a string expression', () => {
		const svx = serializeComponentToSvx('Card', props({ title: '" onclick="alert(1)' }));
		expect(svx).toBe('<Card title={"\\" onclick=\\"alert(1)"} />');
	});

	it('escapes markup and Svelte expressions inside that string', () => {
		const svx = serializeComponentToSvx('Card', props({ title: '</Card><script>{x}</script>' }));
		expect(svx).toBe('<Card title={"\\u003c/Card>\\u003cscript>{x}\\u003c/script>"} />');
		expect(svx).not.toContain('<script');
		// The expression is a JavaScript string literal holding the original value.
		const literal = svx.slice('<Card title={'.length, -' />'.length - 1);
		expect(JSON.parse(literal)).toBe('</Card><script>{x}</script>');
	});

	it('writes ampersands and braces as expressions too', () => {
		expect(serializeComponentToSvx('Card', props({ q: 'a&b' }))).toBe('<Card q={"a&b"} />');
		expect(serializeComponentToSvx('Card', props({ q: '{secret}' }))).toBe('<Card q={"{secret}"} />');
	});

	it('drops a prop whose name is not a single attribute name', () => {
		expect(serializeComponentToSvx('Card', props({ 'a b': 'x', 'x"><img': 'y', ok: 'z' }))).toBe(
			'<Card ok="z" />'
		);
	});

	it('wraps content in an open and close tag', () => {
		expect(serializeComponentToSvx('Card', props({ tone: 'info' }), 'Body')).toBe(
			'<Card tone="info">\nBody\n</Card>'
		);
	});
});
