import type {
	AlertRuleResponse,
	LeafTransitionLog as ApiLeafTransitionLog,
} from "$api-clients";
import type { ConditionNode } from "./types";

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

export interface PreparedTransitionPoint {
	atMs: number;
	value: boolean;
}

/**
 * Wraps the replay endpoint's sparse transition log into a binary-searchable
 * structure keyed by (ruleId, leafId). Points are first-state-then-flip
 * encoded, matching the backend emission contract.
 */
export class LeafTransitionLog {
	private readonly byRuleLeaf = new Map<
		string,
		Map<number, PreparedTransitionPoint[]>
	>();

	constructor(byRule: Record<string, ApiLeafTransitionLog[] | undefined>) {
		for (const ruleId of Object.keys(byRule)) {
			const logs = byRule[ruleId];
			if (!logs) continue;
			const perLeaf = new Map<number, PreparedTransitionPoint[]>();
			for (const log of logs) {
				if (log.leafId === undefined) continue;
				const points: PreparedTransitionPoint[] = [];
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

	/** Read-only view of all transition points for (ruleId, leafId), or empty if none. */
	pointsFor(
		ruleId: string,
		leafId: number,
	): readonly PreparedTransitionPoint[] {
		return this.byRuleLeaf.get(ruleId)?.get(leafId) ?? [];
	}
}

// ---------------------------------------------------------------------------
// Rule composition
// ---------------------------------------------------------------------------

const IN_PROGRESS: unique symbol = Symbol("composing");
type MemoEntry = boolean | typeof IN_PROGRESS;

export interface ComposeOpts {
	ruleById: Map<string, AlertRuleResponse>;
	/**
	 * Caller-owned parsed condition trees keyed by rule id. The composer walks
	 * these references directly; it does NOT reparse `rule.conditionParams`.
	 * Every rule reachable from the entry rule via `alert_state` references
	 * MUST have an entry here, or the recursion will throw.
	 */
	treeByRule: Map<string, ConditionNode>;
	/**
	 * Per-rule map from a leaf node's editor `_uid` to its sequential leaf id
	 * (as produced by <c>assignLeafIds</c> on the same tree from
	 * <c>treeByRule</c>). Same coverage requirement as <c>treeByRule</c>.
	 */
	leafIdsByRule: Map<string, Map<string, number>>;
	disabledRuleIds: ReadonlySet<string>;
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
 * <paramref name="atMs"/> by walking <paramref name="tree"/> and looking up
 * each leaf in <paramref name="log"/>.
 *
 * The caller owns parsing: pass the same parsed tree reference (and the same
 * `leafIdsByRule[rule.id]` map derived from it via <c>assignLeafIds</c>) for
 * as long as you want stable IDs. For rules referenced via `alert_state`,
 * the caller must also pre-populate <c>opts.treeByRule</c> and
 * <c>opts.leafIdsByRule</c> entries for the referenced rule id, otherwise
 * the composer throws.
 *
 * Memoisation is per-timestamp; if the same rule is re-encountered while its
 * own composition is in flight (an `alert_state` cycle) the in-progress
 * sentinel short-circuits to <c>false</c>.
 */
export function composeRuleTruth(
	rule: AlertRuleResponse,
	tree: ConditionNode,
	log: LeafTransitionLog,
	atMs: number,
	opts: ComposeOpts,
): boolean {
	const memo = opts.memo ?? new Map<string, MemoEntry>();
	return composeRuleInternal(rule, tree, log, atMs, opts, memo);
}

function composeRuleInternal(
	rule: AlertRuleResponse,
	tree: ConditionNode,
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

	const result = evalNode(tree, rule, log, atMs, opts, memo);
	memo.set(ruleId, result);
	return result;
}

function leafIdsFor(
	rule: AlertRuleResponse,
	opts: ComposeOpts,
): Map<string, number> {
	const ruleId = rule.id;
	if (!ruleId) {
		throw new Error(
			"composeRuleTruth: rule has no id — cannot resolve leaf ids",
		);
	}
	const idMap = opts.leafIdsByRule.get(ruleId);
	if (!idMap) {
		throw new Error(
			`composeRuleTruth: leafIdsByRule missing entry for rule ${ruleId} — caller must call assignLeafIds first`,
		);
	}
	return idMap;
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
	const idMap = leafIdsFor(rule, opts);
	const leafId = idMap.get(node._uid);
	if (leafId === undefined) return false;
	const v = log.valueAt(rule.id, leafId, atMs);
	return v ?? false;
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
		if (!rule.id || !p.child._uid) return false;
		const idMap = leafIdsFor(rule, opts);
		const leafId = idMap.get(p.child._uid);
		if (leafId === undefined) return false;
		// Current value must be true; previous transition (if any) defines when
		// it became true.
		const nowVal = log.valueAt(rule.id, leafId, atMs);
		if (nowVal !== true) return false;
		const since = mostRecentTrueSince(log, rule.id, leafId, atMs);
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
	const points = log.pointsFor(ruleId, leafId);
	if (points.length === 0) return null;
	if (atMs < points[0].atMs) return null;
	// Find the index whose atMs <= atMs.
	let lo = 0;
	let hi = points.length - 1;
	while (lo < hi) {
		const mid = (lo + hi + 1) >>> 1;
		if (points[mid].atMs <= atMs) lo = mid;
		else hi = mid - 1;
	}
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
	const targetTree = opts.treeByRule.get(p.alert_id);
	if (!targetTree) {
		throw new Error(
			`composeRuleTruth: treeByRule missing entry for referenced rule ${p.alert_id} — caller must pre-parse all reachable rules`,
		);
	}

	// `acknowledged` / `unacknowledged` are runtime concerns the replay log
	// can't reconstruct — collapse both to the same firing-truth as the live
	// `firing` state for now. Documented limitation.
	const nowTrue = composeRuleInternal(target, targetTree, log, atMs, opts, memo);
	if (!nowTrue) return false;

	if (p.for_minutes && p.for_minutes > 0) {
		const boundary = atMs - p.for_minutes * 60_000;
		// Boundary-sampling, same loosening as sustained-of-composite.
		const boundaryMemo = new Map<string, MemoEntry>();
		const boundaryTrue = composeRuleInternal(
			target,
			targetTree,
			log,
			boundary,
			{ ...opts, memo: boundaryMemo },
			boundaryMemo,
		);
		if (!boundaryTrue) return false;
	}
	return true;
}
