import { describe, expect, it } from 'vitest';
import { buildMessages, messageKey, parsePo, unescapePo } from './po';

const CATALOG = `msgid ""
msgstr ""
"Content-Type: text/plain; charset=UTF-8\\n"
"Plural-Forms: nplurals=3; plural=(n==1 ? 0 : n<5 ? 1 : 2);\\n"

#: src/routes/+page.svelte
msgid "Hello"
msgstr "Bonjour"

#: src/routes/+page.svelte
msgctxt "greeting"
msgid "Welcome"
msgstr ""

#, fuzzy
msgid "Save changes"
msgstr "stale"

msgid "One item"
msgid_plural "{0} items"
msgstr[0] "Un"
msgstr[1] "Quelques"
msgstr[2] "Beaucoup"

msgid ""
"A long message that "
"spans lines"
msgstr ""
"une traduction "
"longue"

#~ msgid "Obsolete"
#~ msgstr "Vieux"
`;

describe('parsePo', () => {
	const catalog = parsePo(CATALOG);

	it('reads nplurals from the header and skips it as an entry', () => {
		expect(catalog.nplurals).toBe(3);
		expect(catalog.entries.some((e) => e.msgid === '')).toBe(false);
	});

	it('parses simple, contexted, fuzzy, plural and multiline entries', () => {
		expect(catalog.entries).toHaveLength(5);

		const hello = catalog.entries.find((e) => e.msgid === 'Hello');
		expect(hello?.msgstr).toEqual(['Bonjour']);

		const welcome = catalog.entries.find((e) => e.msgid === 'Welcome');
		expect(welcome?.context).toBe('greeting');
		expect(welcome?.msgstr).toEqual(['']);

		const fuzzy = catalog.entries.find((e) => e.msgid === 'Save changes');
		expect(fuzzy?.fuzzy).toBe(true);

		const plural = catalog.entries.find((e) => e.msgid === 'One item');
		expect(plural?.msgidPlural).toBe('{0} items');
		expect(plural?.msgstr).toEqual(['Un', 'Quelques', 'Beaucoup']);

		const multiline = catalog.entries.find((e) =>
			e.msgid.startsWith('A long message'),
		);
		expect(multiline?.msgid).toBe('A long message that spans lines');
		expect(multiline?.msgstr).toEqual(['une traduction longue']);
	});

	it('skips obsolete entries', () => {
		expect(catalog.entries.some((e) => e.msgid === 'Obsolete')).toBe(false);
	});

	it('keeps a live entry directly after an obsolete block with no separator', () => {
		const parsed = parsePo(
			`#~ msgid "Old"\n#~ msgstr "Vieux"\nmsgid "Live"\nmsgstr "Vivant"\n`,
		);
		expect(parsed.entries).toHaveLength(1);
		expect(parsed.entries[0].msgid).toBe('Live');
	});

	it('does not hang on malformed lines', () => {
		const parsed = parsePo(`msgstr\nmsgid "A"\nmsgstr "B"\ngarbage line\nmsgid "C"\nmsgstr\n`);
		expect(parsed.entries.map((e) => e.msgid)).toContain('A');
	});

	it('unescapes po escape sequences', () => {
		expect(unescapePo('a\\nb\\t\\"c\\"\\\\d')).toBe('a\nb\t"c"\\d');
	});
});

describe('buildMessages', () => {
	it('overlays target translations onto the source message list', () => {
		const source = parsePo(`msgid "Hello"\nmsgstr "Hello"\n\nmsgid "New"\nmsgstr "New"\n`);
		const target = parsePo(`msgid "Hello"\nmsgstr "Bonjour"\n`);

		const messages = buildMessages(source, target);

		expect(messages).toHaveLength(2);
		expect(messages[0].upstream).toEqual(['Bonjour']);
		expect(messages[1].upstream).toEqual(['']);
	});

	it('sizes plural upstream arrays to the target nplurals', () => {
		const source = parsePo(`msgid "One"\nmsgid_plural "Many"\nmsgstr[0] "One"\nmsgstr[1] "Many"\n`);
		const target = parsePo(
			`msgid ""\nmsgstr ""\n"Plural-Forms: nplurals=3; plural=(n);\\n"\n\nmsgid "One"\nmsgid_plural "Many"\nmsgstr[0] "Jeden"\nmsgstr[1] ""\nmsgstr[2] ""\n`,
		);

		const messages = buildMessages(source, target);

		expect(messages[0].forms).toBe(3);
		expect(messages[0].upstream).toEqual(['Jeden', '', '']);
	});

	it('keys contexted messages distinctly', () => {
		expect(messageKey('a', 'x')).not.toBe(messageKey('', 'x'));
		expect(messageKey('', 'x')).toBe('x');
	});
});
