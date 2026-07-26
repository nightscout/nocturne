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

const GREEN = "bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300";
const BLUE = "bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-300";
const YELLOW =
	"bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-300";
const RED = "bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300";
const GREY = "bg-gray-100 text-gray-800 dark:bg-gray-900 dark:text-gray-300";

const MATCH_TYPES: Record<number, MatchTypeDisplay> = {
	0: { label: "Perfect", longLabel: "Perfect Match", class: GREEN },
	1: { label: "Minor Diff", longLabel: "Minor Differences", class: BLUE },
	2: { label: "Major Diff", longLabel: "Major Differences", class: YELLOW },
	3: { label: "Critical", longLabel: "Critical Differences", class: RED },
	4: { label: "NS Missing", longLabel: "Nightscout Missing", class: GREY },
	5: { label: "Nocturne Missing", longLabel: "Nocturne Missing", class: GREY },
	6: { label: "Both Missing", longLabel: "Both Missing", class: GREY },
	7: { label: "Error", longLabel: "Comparison Error", class: RED },
};

/**
 * An unrecognised or absent value is reported as unknown. Falling through to
 * the first entry would show a value we cannot interpret as a green pass.
 */
const UNKNOWN: MatchTypeDisplay = {
	label: "Unknown",
	longLabel: "Unknown Result",
	class: GREY,
};

export function getMatchTypeDisplay(
	matchType: number | undefined
): MatchTypeDisplay {
	if (matchType === undefined) return UNKNOWN;
	return MATCH_TYPES[matchType] ?? UNKNOWN;
}
