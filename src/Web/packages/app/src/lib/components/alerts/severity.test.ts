import { describe, expect, it } from "vitest";
import { severity, severityLabel, severityVar } from "./severity";

describe("severity", () => {
	it("returns slot classes for each known severity", () => {
		expect(severity("critical", "dot")).toBe("bg-status-critical");
		expect(severity("warning", "chip")).toBe(
			"bg-status-warning/15 text-status-warning",
		);
		expect(severity("info", "text")).toBe("text-status-info");
		expect(severity("critical", "strip")).toContain("border-status-critical");
	});

	it("falls back to muted classes for unknown severity", () => {
		expect(severity(undefined, "dot")).toBe("bg-muted text-muted-foreground");
		expect(severity("nope", "text")).toBe("text-muted-foreground");
	});
});

describe("severityVar", () => {
	it("returns the CSS variable reference for each severity", () => {
		expect(severityVar("critical")).toBe("var(--status-critical)");
		expect(severityVar("warning")).toBe("var(--status-warning)");
		expect(severityVar("info")).toBe("var(--status-info)");
	});

	it("falls back to muted-foreground for unknown severity", () => {
		expect(severityVar(undefined)).toBe("var(--muted-foreground)");
	});
});

describe("severityLabel", () => {
	it("returns capitalised labels", () => {
		expect(severityLabel("critical")).toBe("Critical");
		expect(severityLabel("warning")).toBe("Warning");
		expect(severityLabel("info")).toBe("Info");
	});

	it("returns the raw input for unknown values", () => {
		expect(severityLabel("custom")).toBe("custom");
		expect(severityLabel(undefined)).toBe("");
	});
});
