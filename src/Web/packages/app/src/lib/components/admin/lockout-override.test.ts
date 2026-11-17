import { describe, it, expect, vi } from "vitest";
import { error } from "@sveltejs/kit";
import { withLockoutOverride } from "./lockout-override";

function refusedUnlessForced() {
  return vi.fn(async (force: boolean) => {
    if (!force) error(409, "Disabling this provider would leave accounts with no way to sign in");
  });
}

describe("withLockoutOverride", () => {
  it("runs once without the override when the API accepts the change", async () => {
    const run = vi.fn(async (_force: boolean) => {});
    const confirmOverride = vi.fn(() => true);

    await expect(withLockoutOverride("Google", run, confirmOverride)).resolves.toBe(true);

    expect(run.mock.calls).toEqual([[false]]);
    expect(confirmOverride).not.toHaveBeenCalled();
  });

  it("asks, naming the provider, and retries with the override when confirmed", async () => {
    const run = refusedUnlessForced();
    const confirmOverride = vi.fn((_message: string) => true);

    await expect(withLockoutOverride("Google", run, confirmOverride)).resolves.toBe(true);

    expect(run.mock.calls).toEqual([[false], [true]]);
    expect(confirmOverride.mock.calls[0][0]).toContain('"Google"');
  });

  it("changes nothing when the override is declined", async () => {
    const run = refusedUnlessForced();

    await expect(withLockoutOverride("Google", run, () => false)).resolves.toBe(false);

    expect(run.mock.calls).toEqual([[false]]);
  });

  it("lets any other failure through without asking", async () => {
    const run = vi.fn(async () => error(500, "boom"));
    const confirmOverride = vi.fn(() => true);

    await expect(withLockoutOverride("Google", run, confirmOverride)).rejects.toMatchObject({ status: 500 });

    expect(confirmOverride).not.toHaveBeenCalled();
  });
});
