import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { beforeAll, describe, expect, it, vi } from "vitest";
import { loadEngine, replay, validate } from "./engine";
import { SCENARIOS, ticksFor } from "./fixtures";
import { PRESETS, presetRule, type PresetId } from "./presets";
import { LEAF_KINDS, OPERATORS, asGroup, collapse, defaultLeaf, fromRow } from "./tree";
import type { CompareOperator, ConditionNode, LeafNode, SimRule } from "./wire";

// The engine the simulator loads from static/ (scripts/build-alerts-engine.mjs), by file URL.
vi.mock("#lib/synced-asset.js", () => ({
	syncedAsset: (file: string) => new URL(`../../../../../static/${file}`, import.meta.url).href,
}));

const WASM = new URL("../../../../../static/alerts-engine/nocturne_alerts_bg.wasm", import.meta.url);

/**
 * Without the engine the suite is skipped, as `pnpm test` is run without a Rust toolchain; CI
 * builds it and sets ALERTS_ENGINE_REQUIRED, so a missing engine fails there instead.
 */
const runs = existsSync(WASM) || process.env.ALERTS_ENGINE_REQUIRED === "true";

function rule(condition: ConditionNode, index: number, autoResolve?: ConditionNode): SimRule {
	return {
		id: `00000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`,
		name: `Rule ${index + 1}`,
		severity: "warning",
		condition,
		autoResolve,
	};
}

/** `leaf` comparing with `operator`, or undefined for a kind that has no comparison. */
function comparing(leaf: LeafNode, operator: CompareOperator): LeafNode | undefined {
	const copy = structuredClone(leaf);
	switch (copy.type) {
		case "threshold":
			copy.threshold.operator = operator;
			return copy;
		case "rate_of_change":
			copy.rate_of_change.operator = operator;
			return copy;
		case "cob":
			copy.cob.operator = operator;
			return copy;
		case "iob":
			copy.iob.operator = operator;
			return copy;
		case "time_since_last_carb":
			copy.time_since_last_carb.operator = operator;
			return copy;
		case "signal_loss":
		case "time_of_day":
			return undefined;
	}
}

const leaves = LEAF_KINDS.map(({ kind }) => defaultLeaf(kind));

/** Each tree the editor can build, as the simulator sends it (`collapse` of the edited group). */
const built: [string, ConditionNode][] = [
	...leaves.map((leaf): [string, ConditionNode] => [leaf.type, collapse(asGroup(leaf))]),
	...leaves.flatMap((leaf) =>
		[false, true].flatMap((negated) =>
			[null, 15].map((minutes): [string, ConditionNode] => [
				`${leaf.type}, negated ${negated}, held ${minutes ?? "no"} minutes`,
				collapse(asGroup(fromRow({ negated, minutes, leaf }))),
			]),
		),
	),
	...leaves.flatMap((leaf) =>
		OPERATORS.flatMap(({ operator }) => {
			const compared = comparing(leaf, operator);
			return compared ? [[`${leaf.type} ${operator}`, compared] satisfies [string, ConditionNode]] : [];
		}),
	),
	...(["and", "or"] as const).map((operator): [string, ConditionNode] => [
		`every kind under ${operator}`,
		collapse({ type: "composite", composite: { operator, conditions: leaves } }),
	]),
	[
		"a group nested in a group",
		collapse({
			type: "composite",
			composite: {
				operator: "or",
				conditions: [
					fromRow({ negated: true, minutes: 15, leaf: defaultLeaf("threshold") }),
					{
						type: "composite",
						composite: { operator: "and", conditions: [defaultLeaf("threshold"), defaultLeaf("rate_of_change")] },
					},
				],
			},
		}),
	],
];

/** Every example rule, as the simulator sends it. */
const presets = Object.keys(PRESETS).map((id, index) => {
	const preset = presetRule(id as PresetId, index);
	return { ...preset, condition: collapse(asGroup(preset.condition)) };
});

describe.skipIf(!runs)("the simulator's trees in the WebAssembly engine", () => {
	beforeAll(() => {
		if (!existsSync(WASM)) {
			throw new Error("static/alerts-engine is not built. Run `pnpm run alerts-engine` in the portal package.");
		}
		// wasm-bindgen's web loader fetches the module next to its script, and Node cannot fetch a file URL.
		vi.stubGlobal("fetch", async (url: URL) =>
			new Response(readFileSync(fileURLToPath(url)), { headers: { "Content-Type": "application/wasm" } }),
		);
	});

	it.each(built)("validates %s with no issue", async (_, condition) => {
		expect(await validate(rule(condition, 0))).toEqual([]);
	});

	it.each(presets.map((preset) => [preset.name, preset] as const))(
		"validates the example rule %s with no issue",
		async (_, preset) => {
			expect(await validate(preset)).toEqual([]);
		},
	);

	it.each(Object.values(SCENARIOS).map((scenario) => [scenario.id, scenario] as const))(
		"replays every example rule and every built tree over %s without skipping one",
		async (_, scenario) => {
			const rules = [...presets, ...built.map(([, condition], i) => rule(condition, presets.length + i))];
			const result = await replay(rules, ticksFor(scenario), scenario.end);

			const skipped = result.ticks.flatMap((tick) => tick.rules.filter((r) => "skipped" in r).map((r) => r.rule_id));
			expect(new Set(skipped)).toEqual(new Set());
			expect(result.order).toHaveLength(rules.length);
			expect(result.events.some((event) => event.kind === "fired")).toBe(true);
		},
	);

	it("closes the auto-resolve example through its auto-resolve tree", async () => {
		const scenario = SCENARIOS["meal-high"];
		const autoResolving = presets.find((preset) => preset.autoResolve !== undefined);
		expect(autoResolving).toBeDefined();
		if (!autoResolving) return;

		const result = await replay([autoResolving], ticksFor(scenario), scenario.end);

		expect(result.events.map((event) => event.kind)).toContain("auto_resolved");
	});

	describe("a schema 1 tree", () => {
		const schemaOne = { type: "threshold", threshold: { direction: "below", value: 70 } };

		it("is unsupported_schema in the body", async () => {
			const engine = await loadEngine();
			const response = JSON.parse(
				engine.validate(
					JSON.stringify({
						schema_version: 1,
						condition_type: schemaOne.type,
						condition_params: schemaOne.threshold,
						auto_resolve_params: null,
					}),
				),
			);
			expect(response).toMatchObject({ ok: true, valid: false });
			expect(response.issues).toEqual([
				{ scope: "condition", path: "threshold", reason: "unsupported_schema", field: "schema_version" },
			]);
		});

		it("is unsupported_schema in the auto-resolve", async () => {
			const engine = await loadEngine();
			const response = JSON.parse(
				engine.validate(
					JSON.stringify({
						schema_version: 1,
						condition_type: "threshold",
						condition_params: { schema_version: 2, operator: ">", value: 250 },
						auto_resolve_params: schemaOne,
					}),
				),
			);
			expect(response).toMatchObject({ ok: true, valid: false });
			expect(response.issues).toEqual([
				{ scope: "auto_resolve", path: "auto_resolve", reason: "unsupported_schema", field: "schema_version" },
			]);
		});

		it("is skipped on every tick of a replay", async () => {
			const engine = await loadEngine();
			const scenario = SCENARIOS["overnight-low"];
			const ticks = ticksFor(scenario);
			const response = JSON.parse(
				engine.replay(
					JSON.stringify({
						schema_version: 1,
						rules: [
							{
								id: rule(defaultLeaf("threshold"), 0).id,
								condition_type: schemaOne.type,
								condition_params: schemaOne.threshold,
								auto_resolve_enabled: false,
								auto_resolve_params: null,
							},
						],
						ticks,
						include_ticks: true,
						until: new Date(scenario.end).toISOString(),
					}),
				),
			);
			expect(response.ok).toBe(true);
			expect(response.ticks).toHaveLength(ticks.length);
			expect(response.ticks.every((tick: { rules: { skipped?: boolean }[] }) => tick.rules[0].skipped === true)).toBe(true);
		});
	});
});
