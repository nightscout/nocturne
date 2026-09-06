import { describe, it, expect } from "vitest";
import { fieldMessages } from "./field-messages";

describe("fieldMessages", () => {
  it("returns an empty list for null and undefined", () => {
    expect(fieldMessages(null)).toEqual([]);
    expect(fieldMessages(undefined)).toEqual([]);
  });

  it("wraps a single string", () => {
    expect(fieldMessages("Username is required")).toEqual([
      "Username is required",
    ]);
  });

  it("ignores blank strings", () => {
    expect(fieldMessages("")).toEqual([]);
    expect(fieldMessages("   ")).toEqual([]);
    expect(fieldMessages(["", "Real problem", "  "])).toEqual(["Real problem"]);
  });

  it("accepts a list of strings", () => {
    expect(fieldMessages(["One", "Two"])).toEqual(["One", "Two"]);
  });

  it("accepts SvelteKit form issues", () => {
    const issues: readonly { message: string; path: (string | number)[] }[] = [
      { message: "Too short", path: ["username"] },
    ];
    expect(fieldMessages(issues)).toEqual(["Too short"]);
  });

  it("accepts Zod issues from FormGuard.issuesFor", () => {
    const issues: readonly { message: string; code: string }[] = [
      { message: "Name must be at least 2 characters", code: "too_small" },
    ];
    expect(fieldMessages(issues)).toEqual([
      "Name must be at least 2 characters",
    ]);
  });

  it("skips issues with no usable message", () => {
    expect(fieldMessages([{ message: "" }, { message: "Kept" }])).toEqual([
      "Kept",
    ]);
  });
});
