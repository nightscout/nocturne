import { describe, it, expect } from "vitest";
import { LEAF_FACTS, FACT_GROUP_ORDER, getFact, isLeafKind } from "./factCatalog";
import { AlertConditionType } from "$api-clients";

describe("factCatalog", () => {
	it("includes a fact for every backend leaf condition kind", () => {
		const structuralKinds = new Set<string>(["composite", "not", "sustained"]);
		const expectedLeaves = Object.values(AlertConditionType).filter(
			(k) => !structuralKinds.has(k),
		);
		const cataloguedKinds = new Set(LEAF_FACTS.map((f) => f.kind));
		const missing = expectedLeaves.filter((k) => !cataloguedKinds.has(k));
		expect(missing).toEqual([]);
	});

	it("classifies wrappers as non-leaf and leaves as leaf", () => {
		expect(isLeafKind("composite")).toBe(false);
		expect(isLeafKind("not")).toBe(false);
		expect(isLeafKind("sustained")).toBe(false);
		expect(isLeafKind("threshold")).toBe(true);
		expect(isLeafKind("do_not_disturb")).toBe(true);
	});

	it("groups every leaf into one of the declared groups", () => {
		const allowed = new Set(FACT_GROUP_ORDER);
		const offenders = LEAF_FACTS.filter((f) => !allowed.has(f.group)).map((f) => f.kind);
		expect(offenders).toEqual([]);
	});

	it("returns the right fact for a known kind", () => {
		const f = getFact("threshold");
		expect(f?.label).toBe("Glucose");
		expect(f?.group).toBe("glucose");
	});
});
