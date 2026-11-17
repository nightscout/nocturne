import { describe, expect, it } from "vitest";
import { load } from "./+page";

const run = (effectivePermissions: string[]) =>
  load({ parent: async () => ({ effectivePermissions }) } as unknown as Parameters<typeof load>[0]);

describe("clock editor load", () => {
  it("sends a caller without sharing.manage back to the clock list", async () => {
    await expect(run(["glucose.read"])).rejects.toMatchObject({
      status: 303,
      location: "/clock",
    });
  });

  it("lets a caller holding sharing.manage in", async () => {
    await expect(run(["sharing.manage"])).resolves.toBeUndefined();
  });
});
