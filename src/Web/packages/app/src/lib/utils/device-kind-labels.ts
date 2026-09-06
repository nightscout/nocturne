/** Human-facing names for the known client-device kinds. */
const KIND_LABELS = new Map<string, string>([
	["companion", "Companion"],
	["prelude", "Prelude"],
]);

/**
 * Display name for a client-device kind. Unknown kinds (e.g. added to the
 * server catalog before this map learns about them) fall back to the
 * title-cased key; a missing kind renders as the generic "Device".
 */
export function deviceKindLabel(kind: string | undefined): string {
	if (!kind) return "Device";
	return KIND_LABELS.get(kind) ?? kind.charAt(0).toUpperCase() + kind.slice(1);
}
