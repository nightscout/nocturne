import { describe, expect, it } from 'vitest';
import type { ConditionNode } from './wire';
import { SCENARIOS, carbsOnBoard, insulinOnBoard, ticksFor, type Scenario } from './fixtures';
import { PRESETS, presetRule } from './presets';
import { chartData, replayResult, ruleResponses } from './replay-view';
import { LEAF_KINDS, asGroup, collapse, defaultLeaf, fromRow, toRow } from './tree';

const MINUTE = 60_000;

function scenario(readings: [number, number][]): Scenario {
	return {
		id: 'brief-dip',
		label: '',
		description: '',
		start: 0,
		end: 30 * MINUTE,
		readings: readings.map(([m, mgdl]) => ({ at: m * MINUTE, mgdl, trendRate: 0 })),
		carbs: [],
		boluses: [],
	};
}

describe('ticksFor', () => {
	it('reads each tick against the newest reading at or before it, however old', () => {
		const ticks = ticksFor(scenario([[5, 100], [10, 90]]));
		expect(ticks.map((t) => t.context.latest_value)).toEqual([undefined, 100, 90, 90, 90, 90]);
		expect(ticks[4].context.last_reading_at).toBe('1970-01-01T00:10:00Z');
	});

	it('reads a tick before the first reading as fresh, not as no reading ever', () => {
		const [first] = ticksFor(scenario([[5, 100]]));
		expect(first.context.last_reading_at).toBe(first.at);
		expect(first.context.latest_timestamp).toBeUndefined();
	});
});

describe('fixtures the docs describe', () => {
	const below70 = (id: keyof typeof SCENARIOS) => SCENARIOS[id].readings.filter((r) => r.mgdl < 70).length;

	it('keeps the brief dip to three readings under 70, ten minutes', () => {
		expect(below70('brief-dip')).toBe(3);
	});

	it('stops readings for the whole of the sensor gap', () => {
		const inGap = SCENARIOS['sensor-gap'].readings.filter(
			(r) => r.at > new Date(2026, 0, 5, 3, 0).getTime() && r.at < new Date(2026, 0, 5, 3, 40).getTime(),
		);
		expect(inGap).toEqual([]);
	});

	it('reads every scenario every 5 minutes, in order', () => {
		for (const s of Object.values(SCENARIOS)) {
			expect(s.readings.every((r, i) => i === 0 || r.at > s.readings[i - 1].at)).toBe(true);
			expect(s.readings.every((r) => (r.at - s.start) % (5 * MINUTE) === 0)).toBe(true);
		}
	});
});

describe('carbs and insulin on board', () => {
	const withTreatments: Scenario = {
		...scenario([[0, 100]]),
		carbs: [{ at: 0, grams: 30, label: '', absorbMinutes: 60 }],
		boluses: [{ at: 0, units: 4 }],
	};

	it('absorbs carbs linearly over their absorption time', () => {
		expect(carbsOnBoard(withTreatments, -MINUTE)).toBe(0);
		expect(carbsOnBoard(withTreatments, 0)).toBe(30);
		expect(carbsOnBoard(withTreatments, 30 * MINUTE)).toBe(15);
		expect(carbsOnBoard(withTreatments, 90 * MINUTE)).toBe(0);
	});

	it('keeps a bolus on board for four hours, falling all the way', () => {
		expect(insulinOnBoard(withTreatments, 0)).toBe(4);
		expect(insulinOnBoard(withTreatments, 120 * MINUTE)).toBeCloseTo(2);
		expect(insulinOnBoard(withTreatments, 240 * MINUTE)).toBe(0);
	});

	it('gives each tick its carbs, insulin and last carb time', () => {
		const [first] = ticksFor(withTreatments);
		expect(first.context).toMatchObject({ cob_grams: 30, iob_units: 4, last_carb_at: '1970-01-01T00:00:00Z' });
		expect(ticksFor(scenario([[0, 100]]))[0].context.last_carb_at).toBeUndefined();
	});
});

describe('replay in the shape the app reads', () => {
	const rules = [presetRule('low', 0), presetRule('high-auto-resolve', 1)];

	it('stores each rule as the API does, the root payload as its params', () => {
		const [low, high] = ruleResponses(rules);
		expect(low).toMatchObject({ conditionType: 'threshold', conditionParams: { direction: 'below', value: 70 }, severity: 'warning' });
		expect(high).toMatchObject({ autoResolveEnabled: true });
	});

	it('names and colours each event by its rule, and leaves out cleared', () => {
		const trace = SCENARIOS['overnight-low'];
		const result = replayResult(trace, rules, [], {
			order: [],
			events: [
				{ at: '2026-01-06T01:00:00Z', rule_id: rules[0].id, kind: 'fired' },
				{ at: '2026-01-06T01:30:00Z', rule_id: rules[0].id, kind: 'cleared' },
			],
			leaf_transitions: [{ rule_id: rules[0].id, leaves: [{ leaf_id: 0, points: [{ at_ms: 1, value: true }] }] }],
			ticks: [],
		});
		expect(result.events).toEqual([
			{ at: '2026-01-06T01:00:00Z', ruleId: rules[0].id, ruleName: 'Low', severity: 'warning', kind: 'fired' },
		]);
		expect(result.leafTransitionsByRule?.[rules[0].id]).toEqual([{ leafId: 0, points: [{ atMs: 1, value: true }] }]);
		expect(result.windowStart).toBe(new Date(trace.start).toISOString());
	});

	it('stores every leaf kind with its own payload as the params', () => {
		const leaves = LEAF_KINDS.map(({ kind }, i) => ({ id: `r${i}`, name: kind, severity: 'info' as const, condition: defaultLeaf(kind) }));
		const responses = ruleResponses(leaves);
		responses.forEach((response, i) => {
			const leaf = leaves[i].condition as Record<string, unknown>;
			expect(response.conditionType).toBe(leaf.type);
			expect(response.conditionParams).toEqual(leaf[leaf.type as string]);
		});
		const wrapped = ruleResponses([
			{ id: 'n', name: 'not', severity: 'critical', condition: { type: 'not', not: { child: defaultLeaf('iob') } } },
			{ id: 's', name: 'held', severity: 'warning', condition: { type: 'sustained', sustained: { minutes: 5, child: defaultLeaf('cob') } } },
		]);
		expect(wrapped.map((r) => r.conditionType)).toEqual(['not', 'sustained']);
		expect(wrapped[0]).toMatchObject({ severity: 'critical', autoResolveEnabled: false });
	});

	it('times each fact a leaf reads, leaving out ticks where it is unknown', () => {
		const trace: Scenario = {
			...scenario([[5, 100], [10, 90]]),
			carbs: [{ at: 10 * MINUTE, grams: 20, label: 'Juice', absorbMinutes: 60 }],
			boluses: [{ at: 0, units: 2 }],
		};
		const { factTimelines } = replayResult(trace, [], ticksFor(trace), { order: [], events: [], leaf_transitions: [], ticks: [] });
		expect(factTimelines?.latest_glucose.map((p) => p.value)).toEqual([100, 90, 90, 90, 90]);
		expect(factTimelines?.trend_rate).toHaveLength(5);
		expect(factTimelines?.iob).toHaveLength(6);
		expect(factTimelines?.cob.map((p) => p.value).slice(0, 3)).toEqual([0, 0, 20]);
		expect(factTimelines?.time_since_last_carb_minutes.map((p) => p.value)).toEqual([0, 5, 10, 15]);
	});

	it('draws the scenario as the app would chart it', () => {
		const trace: Scenario = {
			...scenario([[0, 100], [5, 120]]),
			carbs: [{ at: 0, grams: 30, label: 'Toast', absorbMinutes: 60 }],
			boluses: [{ at: 0, units: 3 }],
		};
		const data = chartData(trace);
		expect(data.glucoseData.map((p) => p.sgv)).toEqual([100, 120]);
		expect(data.carbMarkers).toMatchObject([{ carbs: 30, label: 'Toast', treatmentId: 'carbs-0' }]);
		expect(data.bolusMarkers).toMatchObject([{ insulin: 3, treatmentId: 'bolus-0' }]);
		expect(data.iobSeries[0].value).toBe(3);
		expect(data.maxCob).toBe(30);
		expect(data.maxIob).toBe(3);
	});
});

describe('tree', () => {
	const below = (value: number): ConditionNode => ({ type: 'threshold', threshold: { direction: 'below', value } });
	const tree: ConditionNode = {
		type: 'composite',
		composite: {
			operator: 'or',
			conditions: [
				{ type: 'not', not: { child: { type: 'sustained', sustained: { minutes: 15, child: below(70) } } } },
				{ type: 'composite', composite: { operator: 'and', conditions: [below(100), below(90)] } },
			],
		},
	};

	it('round-trips a wrapped row in the order the app nests it', () => {
		const [first] = tree.type === 'composite' ? tree.composite.conditions : [];
		const row = toRow(first);
		expect(row).toMatchObject({ negated: true, minutes: 15 });
		expect(row && fromRow(row)).toEqual(first);
	});

	it('edits a bare condition as a group and saves a one-row group as the row', () => {
		const group = asGroup(below(70));
		expect(group.composite.conditions).toHaveLength(1);
		expect(collapse(group)).toEqual(below(70));
	});

	it('gives every preset a tree the editor can show row by row', () => {
		for (const preset of Object.values(PRESETS)) {
			const group = asGroup(preset.condition);
			for (const child of group.composite.conditions) {
				if (child.type !== 'composite') expect(toRow(child)).not.toBeNull();
			}
		}
	});
});
