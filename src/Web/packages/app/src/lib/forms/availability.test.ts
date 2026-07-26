import { describe, it, expect, beforeEach, vi } from "vitest";

/**
 * `debounceOverride` stands in for "the debounce hasn't settled on the current
 * keystroke yet"; null means it has settled on the latest value.
 */
let debounceOverride: string | null = null;

vi.mock("runed", () => ({
  Debounced: class {
    #fn: () => string;
    constructor(fn: () => string, _delay: number) {
      this.#fn = fn;
    }
    get current(): string {
      return debounceOverride ?? this.#fn();
    }
  },
}));

import { useAvailability, type AvailabilityQuery } from "./availability.svelte";

function query(partial: Partial<AvailabilityQuery>): AvailabilityQuery {
  return { loading: false, ...partial };
}

describe("useAvailability", () => {
  beforeEach(() => {
    debounceOverride = null;
  });

  it("is idle for an empty value", () => {
    const availability = useAvailability(
      () => "",
      () => query({ current: { isValid: true } }),
      { label: "Slug" }
    );

    expect(availability.validating).toBe(false);
    expect(availability.error).toBeNull();
    expect(availability.valid).toBe(false);
  });

  it("rejects values shorter than the minimum without asking the server", () => {
    let asked = false;
    const availability = useAvailability(
      () => "ab",
      () => {
        asked = true;
        return query({ current: { isValid: true } });
      },
      { label: "Slug" }
    );

    expect(availability.error).toBe("Slug must be at least 3 characters");
    expect(availability.valid).toBe(false);
    expect(asked).toBe(false);
  });

  it("uses the label in the minimum-length message", () => {
    const availability = useAvailability(
      () => "ab",
      () => query({}),
      { label: "Username", minLength: 4 }
    );

    expect(availability.error).toBe("Username must be at least 4 characters");
  });

  it("validates while the debounce has not settled", () => {
    debounceOverride = "old-value";
    const availability = useAvailability(
      () => "new-value",
      () => query({ current: { isValid: true } }),
      { label: "Slug" }
    );

    expect(availability.validating).toBe(true);
    expect(availability.error).toBeNull();
    expect(availability.valid).toBe(false);
  });

  it("validates while the request is in flight", () => {
    const availability = useAvailability(
      () => "myslug",
      () => query({ loading: true }),
      { label: "Slug" }
    );

    expect(availability.validating).toBe(true);
    expect(availability.valid).toBe(false);
  });

  it("validates while the result has not arrived", () => {
    const availability = useAvailability(
      () => "myslug",
      () => query({ current: null }),
      { label: "Slug" }
    );

    expect(availability.validating).toBe(true);
  });

  it("is valid when the server says the value is free", () => {
    const availability = useAvailability(
      () => "myslug",
      () => query({ current: { isValid: true } }),
      { label: "Slug" }
    );

    expect(availability.validating).toBe(false);
    expect(availability.error).toBeNull();
    expect(availability.valid).toBe(true);
  });

  it("surfaces the server's rejection message", () => {
    const availability = useAvailability(
      () => "myslug",
      () => query({ current: { isValid: false, message: "Already taken" } }),
      { label: "Slug" }
    );

    expect(availability.error).toBe("Already taken");
    expect(availability.valid).toBe(false);
  });

  it("falls back when the server rejects without a message", () => {
    const availability = useAvailability(
      () => "myname",
      () => query({ current: { isValid: false } }),
      { label: "Username" }
    );

    expect(availability.error).toBe("Invalid username");
  });

  it("reports a failed check without claiming the value is taken", () => {
    const availability = useAvailability(
      () => "myslug",
      () => query({ error: new Error("offline"), current: { isValid: true } }),
      { label: "Slug" }
    );

    expect(availability.error).toBe("Could not validate slug");
    expect(availability.valid).toBe(false);
  });

  it("tracks the value it is given", () => {
    let value = "";
    const availability = useAvailability(
      () => value,
      (v) => query({ current: { isValid: v === "free", message: "Already taken" } }),
      { label: "Slug" }
    );

    expect(availability.valid).toBe(false);

    value = "free";
    expect(availability.valid).toBe(true);

    value = "taken";
    expect(availability.valid).toBe(false);
    expect(availability.error).toBe("Already taken");
  });
});
