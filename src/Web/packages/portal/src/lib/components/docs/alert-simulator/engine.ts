import { base } from "$app/paths";
import { wirePayload, type ReplayResult, type ReplayTick, type SimRule, type ValidationIssue } from "./wire";

interface EngineModule {
	default: () => Promise<unknown>;
	replay(requestJson: string): string;
	validate(requestJson: string): string;
}

type Envelope<T> = ({ ok: true } & T) | { ok: false; error: string };

let loading: Promise<EngineModule> | undefined;

/**
 * Served from static/ rather than bundled (scripts/build-alerts-engine.mjs), so a portal built
 * without a Rust toolchain still typechecks and prerenders; only the demo is missing.
 */
export function loadEngine(): Promise<EngineModule> {
	loading ??= (async () => {
		const url = `${base}/alerts-engine/nocturne_alerts.js`;
		const engine: EngineModule = await import(/* @vite-ignore */ url);
		await engine.default();
		return engine;
	})();
	loading.catch(() => (loading = undefined));
	return loading;
}

function unwrap<T>(json: string): T {
	const envelope: Envelope<T> = JSON.parse(json);
	if (!envelope.ok) throw new Error(envelope.error);
	return envelope;
}

/** The root node's payload is the rule's `condition_params`, as the API stores a rule. */
function wireRule(rule: SimRule) {
	return {
		id: rule.id,
		condition_type: rule.condition.type,
		condition_params: wirePayload(rule.condition),
		auto_resolve_enabled: rule.autoResolve !== undefined,
		auto_resolve_params: rule.autoResolve ?? null,
	};
}

export async function replay(rules: SimRule[], ticks: ReplayTick[]): Promise<ReplayResult> {
	const engine = await loadEngine();
	return unwrap<ReplayResult>(
		engine.replay(
			JSON.stringify({ schema_version: 1, rules: rules.map(wireRule), ticks, include_ticks: true }),
		),
	);
}

/** The save-time check the API runs, so the demo refuses exactly what a save would. */
export async function validate(rule: SimRule): Promise<ValidationIssue[]> {
	const engine = await loadEngine();
	const { condition_type, condition_params, auto_resolve_params } = wireRule(rule);
	return unwrap<{ issues: ValidationIssue[] }>(
		engine.validate(
			JSON.stringify({ schema_version: 1, condition_type, condition_params, auto_resolve_params }),
		),
	).issues;
}
