import type {
	AlertRuleResponse,
	LeafTransitionLog as ApiLeafTransitionLog,
} from "$api-clients";
import { nodeFromApi, type ConditionNode } from "./types";

// ---------------------------------------------------------------------------
// Leaf identity
// ---------------------------------------------------------------------------

/**
 * Walks <paramref name="rule"/> in DFS pre-order and returns a Map from each
 * leaf node's editor `_uid` to its sequential integer id.
 *
 * Mirrors the backend <c>Nocturne.Core.Models.Alerts.LeafIdentity.Walk</c>:
 * <c>composite</c>/<c>not</c>/<c>sustained</c> are containers (no id), every
 * other node type is a leaf and gets the next id. The IDs returned here MUST
 * line up with the ones the backend emits in
 * <c>AlertReplayResult.leafTransitionsByRule</c>; if you change the order
 * here, change the C# walker too.
 */
export function assignLeafIds(rule: ConditionNode): Map<string, number> {
	const out = new Map<string, number>();
	const state = { next: 0 };
	walk(rule, state, out);
	return out;
}

function walk(
	node: ConditionNode,
	state: { next: number },
	out: Map<string, number>,
): void {
	switch (node.type) {
		case "composite":
			if (node.composite?.conditions) {
				for (const child of node.composite.conditions) walk(child, state, out);
			}
			return;
		case "not":
			if (node.not?.child) walk(node.not.child, state, out);
			return;
		case "sustained":
			if (node.sustained?.child) walk(node.sustained.child, state, out);
			return;
		default: {
			const id = state.next++;
			if (node._uid) out.set(node._uid, id);
			return;
		}
	}
}

// ---------------------------------------------------------------------------
// Transition log lookup
// ---------------------------------------------------------------------------

interface PreparedPoint {
	atMs: number;
	value: boolean;
}

/**
 * Wraps the replay endpoint's sparse transition log into a binary-searchable
 * structure keyed by (ruleId, leafId). Points are first-state-then-flip
 * encoded, matching the backend emission contract.
 */
export class LeafTransitionLog {
	private readonly byRuleLeaf = new Map<string, Map<number, PreparedPoint[]>>();

	constructor(byRule: Record<string, ApiLeafTransitionLog[] | undefined>) {
		for (const ruleId of Object.keys(byRule)) {
			const logs = byRule[ruleId];
			if (!logs) continue;
			const perLeaf = new Map<number, PreparedPoint[]>();
			for (const log of logs) {
				if (log.leafId === undefined) continue;
				const points: PreparedPoint[] = [];
				for (const p of log.points ?? []) {
					if (p.atMs === undefined || p.value === undefined) continue;
					points.push({ atMs: p.atMs, value: p.value });
				}
				points.sort((a, b) => a.atMs - b.atMs);
				perLeaf.set(log.leafId, points);
			}
			this.byRuleLeaf.set(ruleId, perLeaf);
		}
	}

	/**
	 * Returns the leaf's value as of <paramref name="atMs"/>, or
	 * <c>undefined</c> when no data is available (no points for this
	 * (ruleId, leafId), or the query precedes the first emitted point).
	 *
	 * The backend emits an initial-state point at the window start, so an
	 * undefined return for an in-window query usually means the leaf wasn't
	 * referenced by the rule at all and the caller should treat it as "no
	 * info" rather than "false".
	 */
	valueAt(ruleId: string, leafId: number, atMs: number): boolean | undefined {
		const points = this.byRuleLeaf.get(ruleId)?.get(leafId);
		if (!points || points.length === 0) return undefined;
		if (atMs < points[0].atMs) return undefined;

		// Binary search for the largest index with atMs <= queryMs.
		let lo = 0;
		let hi = points.length - 1;
		while (lo < hi) {
			const mid = (lo + hi + 1) >>> 1;
			if (points[mid].atMs <= atMs) lo = mid;
			else hi = mid - 1;
		}
		return points[lo].value;
	}
}

// ---------------------------------------------------------------------------
// Rule composition
// ---------------------------------------------------------------------------

const IN_PROGRESS: unique symbol = Symbol("composing");
type MemoEntry = boolean | typeof IN_PROGRESS;

export interface ComposeOpts {
	ruleById: Map<string, AlertRuleResponse>;
	disabledRuleIds: ReadonlySet<string>;
	leafIdsByRule: Map<string, Map<string, number>>;
	/**
	 * Per-`atMs` cache of rule-level results. Caller MUST instantiate a fresh
	 * Map (or omit) per timestamp — sharing across times would return stale
	 * truth for `alert_state` references. The composer also stores an
	 * in-progress sentinel to break diamonds and cycles.
	 */
	memo?: Map<string, MemoEntry>;
}

/**
 * Returns the composite truth of <paramref name="rule"/> at
 * <paramref name="atMs"/> by walking its condition tree and looking up each
 * leaf in <paramref name="log"/>.
 *
 * Memoisation is per-timestamp; if the same rule is re-encountered while its
 * own composition is in flight (an `alert_state` cycle) the in-progress
 * sentinel short-circuits to <c>false</c>.
 */
export function composeRuleTruth(
	rule: AlertRuleResponse,
	log: LeafTransitionLog,
	atMs: number,
	opts: ComposeOpts,
): boolean {
	const memo = opts.memo ?? new Map<string, MemoEntry>();
	return composeRuleInternal(rule, log, atMs, opts, memo);
}

function composeRuleInternal(
	rule: AlertRuleResponse,
	log: LeafTransitionLog,
	atMs: number,
	opts: ComposeOpts,
	memo: Map<string, MemoEntry>,
): boolean {
	const ruleId = rule.id;
	if (!ruleId) return false;
	const cached = memo.get(ruleId);
	if (cached === IN_PROGRESS) return false;
	if (cached !== undefined) return cached;
	memo.set(ruleId, IN_PROGRESS);

	const tree = getCachedTree(rule);
	const result = tree
		? evalNode(tree, rule, log, atMs, opts, memo)
		: false;
	memo.set(ruleId, result);
	return result;
}

// Cache the parsed tree per AlertRuleResponse so its uids stay stable across
// the multiple internal walks (composition + leaf-id sequence lookup).
// nodeFromApi assigns fresh uids on every call, so without this cache the
// leaf node walked by composition wouldn't match the leaf-id sequence.
const PARSED_TREE = new WeakMap<AlertRuleResponse, ConditionNode | null>();

function getCachedTree(rule: AlertRuleResponse): ConditionNode | null {
	let cached = PARSED_TREE.get(rule);
	if (cached === undefined) {
		cached = nodeFromApi(rule.conditionType, rule.conditionParams);
		PARSED_TREE.set(rule, cached);
	}
	return cached;
}

function evalNode(
	node: ConditionNode,
	rule: AlertRuleResponse,
	log: LeafTransitionLog,
	atMs: number,
	opts: ComposeOpts,
	memo: Map<string, MemoEntry>,
): boolean {
	switch (node.type) {
		case "composite": {
			const p = node.composite;
			if (!p || p.conditions.length === 0) return false;
			if (p.operator === "and") {
				for (const c of p.conditions) {
					if (!evalNode(c, rule, log, atMs, opts, memo)) return false;
				}
				return true;
			}
			for (const c of p.conditions) {
				if (evalNode(c, rule, log, atMs, opts, memo)) return true;
			}
			return false;
		}
		case "not":
			if (!node.not?.child) return false;
			return !evalNode(node.not.child, rule, log, atMs, opts, memo);
		case "sustained":
			return evalSustained(node, rule, log, atMs, opts, memo);
		case "alert_state":
			return evalAlertState(node, log, atMs, opts, memo);
		default:
			return evalLeaf(node, rule, log, atMs, opts);
	}
}

function evalLeaf(
	node: ConditionNode,
	rule: AlertRuleResponse,
	log: LeafTransitionLog,
	atMs: number,
	opts: ComposeOpts,
): boolean {
	if (!rule.id || !node._uid) return false;
	// The original `rule.conditionType`/`conditionParams` was re-parsed via
	// nodeFromApi, which assigns brand-new uids — so we can't look up by the
	// node's own _uid here. Instead, rebuild leafIds from the parsed tree by
	// matching DFS order against the cached map. To keep this O(1) per leaf, we
	// memoise the parsed-tree leaf id sequence on first use.
	const ids = getLeafIdSequenceForRule(rule, opts);
	const idx = ids.indexOf(node._uid);
	if (idx < 0) return false;
	const leafId = idx;
	const v = log.valueAt(rule.id, leafId, atMs);
	return v ?? false;
}

// Sequence of leaf _uids in DFS pre-order for a rule's parsed tree, cached on
// the opts.leafIdsByRule map so callers can pre-populate it but lazy parsing
// also works.
const PARSED_SEQ = new WeakMap<AlertRuleResponse, string[]>();

function getLeafIdSequenceForRule(
	rule: AlertRuleResponse,
	opts: ComposeOpts,
): string[] {
	const cached = PARSED_SEQ.get(rule);
	if (cached) return cached;
	const tree = getCachedTree(rule);
	const ids = tree ? collectLeafUids(tree) : [];
	PARSED_SEQ.set(rule, ids);
	// Also populate opts so call-sites that introspect leafIdsByRule see it.
	if (rule.id && !opts.leafIdsByRule.has(rule.id)) {
		const m = new Map<string, number>();
		ids.forEach((u, i) => m.set(u, i));
		opts.leafIdsByRule.set(rule.id, m);
	}
	return ids;
}

function collectLeafUids(node: ConditionNode): string[] {
	const out: string[] = [];
	collectLeafUidsInto(node, out);
	return out;
}

function collectLeafUidsInto(node: ConditionNode, out: string[]): void {
	switch (node.type) {
		case "composite":
			if (node.composite?.conditions) {
				for (const c of node.composite.conditions) collectLeafUidsInto(c, out);
			}
			return;
		case "not":
			if (node.not?.child) collectLeafUidsInto(node.not.child, out);
			return;
		case "sustained":
			if (node.sustained?.child) collectLeafUidsInto(node.sustained.child, out);
			return;
		default:
			if (node._uid) out.push(node._uid);
			return;
	}
}

function evalSustained(
	node: ConditionNode,
	rule: AlertRuleResponse,
	log: LeafTransitionLog,
	atMs: number,
	opts: ComposeOpts,
	memo: Map<string, MemoEntry>,
): boolean {
	const p = node.sustained;
	if (!p || !p.child) return false;
	const windowMs = p.minutes * 60_000;
	const boundary = atMs - windowMs;

	// Sustained-of-leaf (the common case): inspect the leaf's transition list
	// directly so we know exactly when it last became true.
	if (isLeaf(p.child)) {
		if (!rule.id) return false;
		const ids = getLeafIdSequenceForRule(rule, opts);
		const idx = p.child._uid ? ids.indexOf(p.child._uid) : -1;
		if (idx < 0) return false;
		// Current value must be true; previous transition (if any) defines when
		// it became true.
		const nowVal = log.valueAt(rule.id, idx, atMs);
		if (nowVal !== true) return false;
		const since = mostRecentTrueSince(log, rule.id, idx, atMs);
		// since === null means the leaf has been true for the entire history we
		// have; treat the window-start sample as the anchor and accept.
		if (since === null) return true;
		return atMs - since >= windowMs;
	}

	// Sustained-of-composite is rare. Boundary-sampling: require the child to
	// be true at both the start of the sustain window and at `atMs`. This is
	// looser than the backend's continuous evaluation but adequate for the
	// replay UI's at-a-glance composition.
	const startTrue = evalNode(p.child, rule, log, boundary, opts, memo);
	if (!startTrue) return false;
	return evalNode(p.child, rule, log, atMs, opts, memo);
}

function isLeaf(node: ConditionNode): boolean {
	return node.type !== "composite" && node.type !== "not" && node.type !== "sustained";
}

function mostRecentTrueSince(
	log: LeafTransitionLog,
	ruleId: string,
	leafId: number,
	atMs: number,
): number | null {
	// Reach into the prepared point list. The class encapsulates points but for
	// sustained evaluation we need the transition timestamp of the most recent
	// flip-to-true. valueAt only exposes the current value, so re-walk via a
	// linear scan back from the query point — there are typically <10 transitions
	// per leaf in a replay window.
	const points = (log as unknown as { byRuleLeaf: Map<string, Map<number, PreparedPoint[]>> })
		.byRuleLeaf.get(ruleId)?.get(leafId);
	if (!points || points.length === 0) return null;
	// Find the index whose atMs <= atMs.
	let lo = 0;
	let hi = points.length - 1;
	while (lo < hi) {
		const mid = (lo + hi + 1) >>> 1;
		if (points[mid].atMs <= atMs) lo = mid;
		else hi = mid - 1;
	}
	if (atMs < points[0].atMs) return null;
	// Walk backwards to find the most recent true-anchor.
	for (let i = lo; i >= 0; i--) {
		if (points[i].value) {
			if (i === 0 || !points[i - 1].value) return points[i].atMs;
			continue;
		}
		// Hit a false before finding a true → leaf isn't currently true.
		return points[i].atMs;
	}
	return null;
}

function evalAlertState(
	node: ConditionNode,
	log: LeafTransitionLog,
	atMs: number,
	opts: ComposeOpts,
	memo: Map<string, MemoEntry>,
): boolean {
	const p = node.alert_state;
	if (!p?.alert_id) return false;
	if (opts.disabledRuleIds.has(p.alert_id)) return false;
	const target = opts.ruleById.get(p.alert_id);
	if (!target) return false;

	// `acknowledged` / `unacknowledged` are runtime concerns the replay log
	// can't reconstruct — collapse both to the same firing-truth as the live
	// `firing` state for now. Documented limitation.
	const nowTrue = composeRuleInternal(target, log, atMs, opts, memo);
	if (!nowTrue) return false;

	if (p.for_minutes && p.for_minutes > 0) {
		const boundary = atMs - p.for_minutes * 60_000;
		// Boundary-sampling, same loosening as sustained-of-composite.
		const boundaryMemo = new Map<string, MemoEntry>();
		const boundaryTrue = composeRuleInternal(
			target,
			log,
			boundary,
			{ ...opts, memo: boundaryMemo },
			boundaryMemo,
		);
		if (!boundaryTrue) return false;
	}
	return true;
}
