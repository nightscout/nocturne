/**
 * Request-scoped element IDs for bits-ui during SSR.
 *
 * bits-ui's `useId` counts up from a counter it keeps on `globalThis`, and the
 * client always starts from 0, so a server render has to start from 0 too or
 * hydration finds different IDs than the markup carries.
 *
 * Assigning a fresh counter to `globalThis` at the start of each request only
 * holds if requests never overlap. They do — Node interleaves them — so one
 * request's reset restarts the counter partway through another's render and the
 * two disagree, which is the intermittent mismatch the reset was added to
 * prevent. Giving each request its own counter is the actual fix.
 */
import { AsyncLocalStorage } from "node:async_hooks";

export interface BitsIdCounter {
	current: number;
}

const store = new AsyncLocalStorage<BitsIdCounter>();

/**
 * Used when no request is in scope, e.g. an ID generated during module
 * initialisation. Nothing hydrates against those.
 */
const fallback: BitsIdCounter = { current: 0 };

/**
 * Point `globalThis.bitsIdCounter` at the calling request's counter.
 *
 * Safe to call more than once, and in either order relative to bits-ui's own
 * `globalThis.bitsIdCounter ??= { current: 0 }`: the property is configurable,
 * so a later definition wins, and the getter never returns nullish so the `??=`
 * is a no-op.
 */
export function installRequestScopedBitsIdCounter(): void {
	Object.defineProperty(globalThis, "bitsIdCounter", {
		configurable: true,
		get: () => store.getStore() ?? fallback,
		// A setter has to exist or bits-ui's `??=` throws in strict mode.
		set: () => {},
	});
}

/** Run `fn` with a counter of its own, starting from 0. */
export function withFreshBitsIdCounter<T>(fn: () => T): T {
	return store.run({ current: 0 }, fn);
}
