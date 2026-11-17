/**
 * Display strings for the Nightscout-vs-Nocturne response comparison result.
 *
 * The list and detail views want different label lengths for the same value, so
 * both live here rather than in two hand-kept copies.
 */
export interface MatchTypeDisplay {
	/** Short label, for a table cell. */
	label: string;
	/** Full label, for a detail page. */
	longLabel: string;
	class: string;
}

const SUCCESS = "bg-success/10 text-success";
const INFO = "bg-info/10 text-info";
const WARNING = "bg-warning/10 text-warning";
const DESTRUCTIVE = "bg-destructive/10 text-destructive";
const MUTED = "bg-muted text-foreground";

const MATCH_TYPES: Record<number, MatchTypeDisplay> = {
	0: { label: "Perfect", longLabel: "Perfect match", class: SUCCESS },
	1: { label: "Minor diff", longLabel: "Minor differences", class: INFO },
	2: { label: "Major diff", longLabel: "Major differences", class: WARNING },
	3: { label: "Critical", longLabel: "Critical differences", class: DESTRUCTIVE },
	4: { label: "NS missing", longLabel: "Nightscout missing", class: MUTED },
	5: { label: "Nocturne missing", longLabel: "Nocturne missing", class: MUTED },
	6: { label: "Both missing", longLabel: "Both missing", class: MUTED },
	7: { label: "Error", longLabel: "Comparison error", class: DESTRUCTIVE },
};

/**
 * An unrecognised or absent value is reported as unknown. Falling through to
 * the first entry would show a value we cannot interpret as a green pass.
 */
const UNKNOWN: MatchTypeDisplay = {
	label: "Unknown",
	longLabel: "Unknown result",
	class: MUTED,
};

export function getMatchTypeDisplay(
	matchType: number | undefined
): MatchTypeDisplay {
	if (matchType === undefined) return UNKNOWN;
	return MATCH_TYPES[matchType] ?? UNKNOWN;
}
