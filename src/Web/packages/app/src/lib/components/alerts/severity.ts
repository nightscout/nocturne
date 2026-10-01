import type { AlertRuleSeverity } from "$api-clients";
import { statusVar, type StatusToken } from "@nocturne/ui/tokens";

export type SeveritySlot = "dot" | "chip" | "strip" | "text";

const CLASS_TABLE: Record<AlertRuleSeverity, Record<SeveritySlot, string>> = {
	critical: {
		dot: "bg-status-critical",
		chip: "bg-status-critical/15 text-status-critical",
		strip: "bg-status-critical/10 border-status-critical/40 text-status-critical",
		text: "text-status-critical",
	},
	warning: {
		dot: "bg-status-warning",
		chip: "bg-status-warning/15 text-status-warning",
		strip: "bg-status-warning/10 border-status-warning/30 text-status-warning",
		text: "text-status-warning",
	},
	info: {
		dot: "bg-status-info",
		chip: "bg-status-info/15 text-status-info",
		strip: "bg-status-info/10 border-status-info/30 text-status-info",
		text: "text-status-info",
	},
};

/**
 * The design-system token each severity reads from. The annotation is what
 * stops this drifting: a severity with no token of that name fails to compile,
 * and the token's value lives once, in `@nocturne/ui/tokens`.
 */
const TOKEN_TABLE: Record<AlertRuleSeverity, StatusToken> = {
	critical: "critical",
	warning: "warning",
	info: "info",
};

const LABEL_TABLE: Record<AlertRuleSeverity, string> = {
	critical: "Critical",
	warning: "Warning",
	info: "Info",
};

function isKnown(s: AlertRuleSeverity | string | undefined): s is AlertRuleSeverity {
	return s === "critical" || s === "warning" || s === "info";
}

/** Tailwind class string for a given severity in a given visual slot. */
export function severity(
	s: AlertRuleSeverity | string | undefined,
	slot: SeveritySlot,
): string {
	if (!isKnown(s)) {
		return slot === "text" ? "text-muted-foreground" : "bg-muted text-muted-foreground";
	}
	return CLASS_TABLE[s][slot];
}

/** Raw CSS variable reference. For chart fills and other non-class consumers. */
export function severityVar(s: AlertRuleSeverity | string | undefined): string {
	return isKnown(s) ? statusVar(TOKEN_TABLE[s]) : "var(--muted-foreground)";
}

/** Human label. */
export function severityLabel(s: AlertRuleSeverity | string | undefined): string {
	return isKnown(s) ? LABEL_TABLE[s] : (s ?? "");
}
