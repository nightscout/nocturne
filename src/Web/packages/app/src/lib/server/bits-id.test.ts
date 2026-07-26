import { describe, it, expect, beforeAll } from "vitest";
import {
	installRequestScopedBitsIdCounter,
	withFreshBitsIdCounter,
} from "./bits-id";

/** What bits-ui's own `useId` does, verbatim. */
function useId(prefix = "bits"): string {
	const counter = (globalThis as unknown as { bitsIdCounter: { current: number } })
		.bitsIdCounter;
	counter.current++;
	return `${prefix}-${counter.current}`;
}

/** Yield to the event loop, as an awaited load does mid-render. */
const tick = () => new Promise((resolve) => setTimeout(resolve, 0));

describe("request-scoped bits-ui id counter", () => {
	beforeAll(() => {
		installRequestScopedBitsIdCounter();
	});

	it("starts each request from the same place the client does", () => {
		expect(withFreshBitsIdCounter(() => [useId(), useId()])).toEqual([
			"bits-1",
			"bits-2",
		]);
		expect(withFreshBitsIdCounter(() => [useId(), useId()])).toEqual([
			"bits-1",
			"bits-2",
		]);
	});

	it("keeps interleaved renders on their own sequences", async () => {
		// The bug this replaces: a second request resetting a shared counter
		// restarted the first request's numbering partway through its render, so
		// the markup carried IDs the client never reproduced.
		const [a, b] = await Promise.all([
			withFreshBitsIdCounter(async () => {
				const first = useId();
				await tick();
				const second = useId();
				await tick();
				return [first, second, useId()];
			}),
			withFreshBitsIdCounter(async () => {
				await tick();
				const first = useId();
				await tick();
				return [first, useId()];
			}),
		]);

		expect(a).toEqual(["bits-1", "bits-2", "bits-3"]);
		expect(b).toEqual(["bits-1", "bits-2"]);
	});

	it("survives bits-ui's own initialiser, whichever order it runs in", () => {
		// `globalThis.bitsIdCounter ??= { current: 0 }` on import must neither
		// throw nor replace the request-scoped accessor.
		expect(() => {
			(globalThis as unknown as { bitsIdCounter: { current: number } })
				.bitsIdCounter ??= { current: 0 };
		}).not.toThrow();

		expect(withFreshBitsIdCounter(() => useId())).toBe("bits-1");
	});

	it("still hands out ids outside a request", () => {
		expect(useId()).toMatch(/^bits-\d+$/);
	});
});
