import { describe, it, expect, vi, beforeEach } from "vitest";
import { flushSync } from "svelte";

// Mock the generated remote functions before importing the state module. WeightState reads
// getBodyWeights() as a reactive query (`.current`) and calls create() on save. The sibling
// classes (ClinicalState et al.) only touch their remote modules at instantiation, so empty
// mocks suffice.
const create = vi.fn().mockResolvedValue({});
let bodyWeightsCurrent: unknown[] | undefined;

vi.mock("$api/generated/bodyWeights.generated.remote", () => ({
  getBodyWeights: () => ({
    get current() {
      return bodyWeightsCurrent;
    },
  }),
  create: (...args: unknown[]) => create(...args),
}));
vi.mock("$api/generated/patientRecords.generated.remote", () => ({}));
vi.mock("$api/generated/insulinCatalogs.generated.remote", () => ({
  getCatalog: () => ({ current: undefined }),
}));

import { WeightState } from "./state.svelte";

/**
 * Instantiate WeightState inside an effect root (its constructor registers a
 * $effect) and flush so the loaded BodyWeight lands in the field before
 * assertions run.
 */
function makeWeightState(): { w: WeightState; cleanup: () => void } {
  let w!: WeightState;
  const cleanup = $effect.root(() => {
    w = new WeightState();
  });
  flushSync();
  return { w, cleanup };
}

describe("WeightState", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    bodyWeightsCurrent = [{ weightKg: 70 }];
  });

  // Regression for nightscout/nocturne#481: bind:value on a type="number" input assigns a
  // number, and dirty's string handling threw "this.weightKg.trim is not a function".
  it("tracks dirty against numeric values from the input binding", () => {
    const { w, cleanup } = makeWeightState();
    expect(w.dirty).toBe(false);

    w.weightKg = 70.5;
    expect(w.dirty).toBe(true);

    w.weightKg = 70;
    expect(w.dirty).toBe(false);
    cleanup();
  });

  it("saves the changed value and resets dirty", async () => {
    const { w, cleanup } = makeWeightState();
    w.weightKg = 72.5;

    await expect(w.save()).resolves.toBe(true);

    expect(create).toHaveBeenCalledTimes(1);
    expect(create).toHaveBeenCalledWith({
      weightKg: 72.5,
      mills: expect.any(Number),
    });
    expect(w.dirty).toBe(false);
    cleanup();
  });

  it("does not create an entry when the input is cleared (null)", async () => {
    const { w, cleanup } = makeWeightState();
    w.weightKg = null;
    expect(w.dirty).toBe(true);

    await expect(w.save()).resolves.toBe(true);

    expect(create).not.toHaveBeenCalled();
    cleanup();
  });

  it("does not create an entry when the value is unchanged", async () => {
    const { w, cleanup } = makeWeightState();

    await expect(w.save()).resolves.toBe(true);

    expect(create).not.toHaveBeenCalled();
    cleanup();
  });
});
