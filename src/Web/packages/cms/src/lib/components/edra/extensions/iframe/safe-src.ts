const FRAMEABLE_PROTOCOLS = new Set(['http:', 'https:']);

/**
 * An iframe's `src` as stored and rendered by the editor, or null when it would run script in the
 * editor's origin (`javascript:`) or load inline content (`data:`, `blob:`). Pasted HTML reaches
 * both the editor's node view and the preview pane, so the check sits on the attribute itself.
 * Relative URLs resolve to the page's own scheme and are kept.
 */
export function safeFrameSrc(value: unknown): string | null {
	if (typeof value !== 'string') return null;
	try {
		return FRAMEABLE_PROTOCOLS.has(new URL(value, 'https://relative.invalid').protocol)
			? value
			: null;
	} catch {
		return null;
	}
}
