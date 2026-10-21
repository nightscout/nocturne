import { describe, expect, it } from "vitest";
import { GoogleHealthOptionsSchema } from "./generated/schemas";

describe("Google Health generated request validation", () => {
  it("preserves the API limit of 32 data types", () => {
    expect(GoogleHealthOptionsSchema.safeParse({ dataTypes: Array(32).fill("steps") }).success).toBe(true);
    expect(GoogleHealthOptionsSchema.safeParse({ dataTypes: Array(33).fill("steps") }).success).toBe(false);
  });
});
