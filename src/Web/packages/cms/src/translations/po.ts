// Browser-safe gettext .po parsing for the in-app translation editor.
// Read-only: edits flow through the drafts API and the server-side catalog
// editor; this module never serializes .po files.
//
// The visual-editor concept and message-model shape are adapted from
// vite-plugin-lingo by Michael-Obele (AGPL-3.0-or-later), rewritten for
// browser use without gettext-parser.

export interface PoEntry {
	msgid: string;
	/** msgctxt; empty string when uncontexted. */
	context: string;
	/** Present only on plural entries. */
	msgidPlural?: string;
	/** One value for singular entries, nplurals for plural ones. */
	msgstr: string[];
	fuzzy: boolean;
}

export interface PoCatalog {
	entries: PoEntry[];
	/** From the header's Plural-Forms; 2 when absent. */
	nplurals: number;
}

export function unescapePo(s: string): string {
	let out = '';
	for (let i = 0; i < s.length; i++) {
		if (s[i] !== '\\' || i + 1 >= s.length) {
			out += s[i];
			continue;
		}
		i++;
		out +=
			s[i] === 'n' ? '\n'
			: s[i] === 't' ? '\t'
			: s[i] === 'r' ? '\r'
			: s[i];
	}
	return out;
}

function quoted(line: string): string {
	const first = line.indexOf('"');
	const last = line.lastIndexOf('"');
	return first >= 0 && last > first ? unescapePo(line.slice(first + 1, last)) : '';
}

export function parsePo(text: string): PoCatalog {
	const lines = text.split('\n').map((l) => l.replace(/\r$/, ''));
	const entries: PoEntry[] = [];
	let nplurals = 2;

	let i = 0;
	while (i < lines.length) {
		if (lines[i].trim().length === 0) {
			i++;
			continue;
		}

		// One block: comments then msgctxt/msgid/msgid_plural/msgstr(s).
		// Obsolete (#~) lines are skipped one by one, never by jumping to the
		// next blank line: a live entry directly after an obsolete block with
		// no separator must not be swallowed.
		let fuzzy = false;
		while (i < lines.length && lines[i].startsWith('#') && !lines[i].startsWith('#~')) {
			if (lines[i].startsWith('#,') && lines[i].includes('fuzzy')) fuzzy = true;
			i++;
		}
		if (i < lines.length && lines[i].startsWith('#~')) {
			while (i < lines.length && lines[i].startsWith('#~')) i++;
			continue;
		}

		const readString = (keyword: string): string | null => {
			if (i >= lines.length || !lines[i].startsWith(keyword)) return null;
			let value = quoted(lines[i].slice(keyword.length));
			i++;
			while (i < lines.length && lines[i].startsWith('"')) {
				value += quoted(lines[i]);
				i++;
			}
			return value;
		};

		const context = readString('msgctxt ') ?? '';
		const msgid = readString('msgid ');
		if (msgid === null) {
			// Stray content: skip one line and resync, so a following valid
			// entry is not swallowed even without blank-line separators.
			i++;
			continue;
		}
		const msgidPlural = readString('msgid_plural ') ?? undefined;

		const msgstr: string[] = [];
		while (i < lines.length && lines[i].startsWith('msgstr')) {
			const value = readString(lines[i].slice(0, lines[i].indexOf('"')));
			msgstr.push(value ?? '');
		}

		if (msgid.length === 0 && context.length === 0) {
			// Header entry: read Plural-Forms.
			const match = msgstr[0]?.match(/nplurals\s*=\s*(\d+)/);
			if (match) nplurals = parseInt(match[1], 10);
			continue;
		}

		entries.push({ msgid, context, msgidPlural, msgstr, fuzzy });
	}

	return { entries, nplurals };
}

export interface TranslationMessage {
	/** Stable key for lookups: context + EOT separator + msgid, per gettext. */
	key: string;
	msgid: string;
	context: string;
	msgidPlural?: string;
	/** Upstream msgstr values from the target catalog ('' when untranslated). */
	upstream: string[];
	fuzzy: boolean;
	/** Number of msgstr slots this message needs. */
	forms: number;
}

export const messageKey = (context: string, msgid: string): string =>
	context.length === 0 ? msgid : context + String.fromCharCode(4) + msgid;

/**
 * Builds the editable message list for a target locale from the source (en)
 * catalog, overlaying the target catalog's existing translations. The source
 * catalog defines which messages exist; target-only entries are stale.
 */
export function buildMessages(source: PoCatalog, target: PoCatalog): TranslationMessage[] {
	const targetByKey = new Map(target.entries.map((e) => [messageKey(e.context, e.msgid), e]));

	return source.entries.map((entry) => {
		const key = messageKey(entry.context, entry.msgid);
		const existing = targetByKey.get(key);
		const forms = entry.msgidPlural ? target.nplurals : 1;
		const upstream = Array.from({ length: forms }, (_, n) => existing?.msgstr[n] ?? '');
		return {
			key,
			msgid: entry.msgid,
			context: entry.context,
			msgidPlural: entry.msgidPlural,
			upstream,
			fuzzy: existing?.fuzzy ?? false,
			forms,
		};
	});
}
