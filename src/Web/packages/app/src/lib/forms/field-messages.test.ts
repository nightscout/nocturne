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
    expect(
      fieldMessages([{ message: "Too short", path: ["username"] }] as never)
    ).toEqual(["Too short"]);
  });

  it("accepts Zod issues from FormGuard.issuesFor", () => {
    expect(
      fieldMessages([
        { message: "Name must be at least 2 characters", code: "too_small" },
      ] as never)
    ).toEqual(["Name must be at least 2 characters"]);
  });

  it("skips issues with no usable message", () => {
    expect(fieldMessages([{ message: "" }, { message: "Kept" }])).toEqual([
      "Kept",
    ]);
  });
});
