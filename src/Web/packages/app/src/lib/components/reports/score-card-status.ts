/**
 * How a glucose statistic is banded on the reports hub.
 *
 * These bands describe the number, not the person: they say where a value sits
 * relative to consensus targets, and the labels are deliberately plain rather
 * than reassuring or alarming.
 */
export type ScoreCardStatus =
	| "excellent"
	| "good"
	| "fair"
	| "needs-attention"
	| "critical";

const LABELS: Record<ScoreCardStatus, string> = {
	excellent: "Excellent",
	good: "Good",
	fair: "Fair",
	"needs-attention": "Needs Attention",
	critical: "Critical",
};

export function scoreCardLabel(status: ScoreCardStatus | undefined): string {
	return LABELS[status ?? "good"];
}
