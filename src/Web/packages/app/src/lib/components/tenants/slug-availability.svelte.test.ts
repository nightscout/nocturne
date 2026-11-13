import { describe, expect, it, vi } from "vitest";
import { flushSync } from "svelte";
import { remoteQuery, remoteQueryError } from "#lib/test-stubs/remote-resource.js";
import { createSlugAvailability } from "./slug-availability.svelte.js";

async function settle(check: () => unknown) {
  await vi.waitFor(() => {
    flushSync();
    expect(check()).not.toEqual({ state: "checking" });
  }, { timeout: 3000 });
}

function track(initial: string, check: (slug: string) => ReturnType<typeof remoteQuery>) {
  let input = $state(initial);
  let availability!: ReturnType<typeof createSlugAvailability>;
  const cleanup = $effect.root(() => {
    availability = createSlugAvailability(() => input, check as never);
  });
  return {
    get current() {
      return availability.current;
    },
    get normalized() {
      return availability.normalized;
    },
    type(value: string) {
      input = value;
      flushSync();
    },
    cleanup,
  };
}

describe("createSlugAvailability", () => {
  it("is empty until something is typed", () => {
    const check = vi.fn();
    const slug = track("", check);

    expect(slug.current).toEqual({ state: "empty" });
    expect(check).not.toHaveBeenCalled();
    slug.cleanup();
  });

  it("lower-cases and trims, and checks only after typing pauses", async () => {
    const check = vi.fn(() => remoteQuery(() => ({ isValid: true })));
    const slug = track("", check);

    slug.type("  Sam ");
    expect(slug.normalized).toBe("sam");
    expect(slug.current).toEqual({ state: "checking" });

    await settle(() => slug.current);
    expect(check).toHaveBeenCalledWith("sam");
    expect(slug.current).toEqual({ state: "available", slug: "sam" });
    slug.cleanup();
  });

  it("reports the API's reason for a refused address", async () => {
    const slug = track("", () => remoteQuery(() => ({ isValid: false, message: "This name is already taken" })));

    slug.type("dev");
    await settle(() => slug.current);

    expect(slug.current).toEqual({ state: "refused", message: "This name is already taken" });
    slug.cleanup();
  });

  it("refuses with a recovery step when the check itself fails", async () => {
    const slug = track("", () => remoteQueryError(new Error("offline")));

    slug.type("sam");
    await settle(() => slug.current);

    expect(slug.current).toEqual({ state: "refused", message: "Could not check the address. Try again." });
    slug.cleanup();
  });
});
