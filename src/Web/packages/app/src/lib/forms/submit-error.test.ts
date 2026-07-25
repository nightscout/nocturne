import { describe, it, expect } from "vitest";
import { describeSubmitError, GENERIC_SUBMIT_ERROR } from "./submit-error";

describe("describeSubmitError", () => {
  it("uses the handler's message for a 4xx", () => {
    const err = { status: 400, body: { message: "Diabetes type is required" } };
    expect(describeSubmitError(err)).toBe("Diabetes type is required");
  });

  it("hides 5xx detail behind the generic message", () => {
    const err = { status: 500, body: { message: "NullReferenceException" } };
    expect(describeSubmitError(err)).toBe(GENERIC_SUBMIT_ERROR);
  });

  it("falls back for a plain thrown Error", () => {
    expect(describeSubmitError(new Error("fetch failed"))).toBe(
      GENERIC_SUBMIT_ERROR
    );
  });

  it("falls back when the 4xx body has no message", () => {
    expect(describeSubmitError({ status: 409, body: {} })).toBe(
      GENERIC_SUBMIT_ERROR
    );
    expect(describeSubmitError({ status: 409, body: { message: "  " } })).toBe(
      GENERIC_SUBMIT_ERROR
    );
  });

  it("uses the caller's fallback", () => {
    expect(describeSubmitError(new Error("x"), "Couldn't save.")).toBe(
      "Couldn't save."
    );
  });

  it("tolerates null and non-objects", () => {
    expect(describeSubmitError(null)).toBe(GENERIC_SUBMIT_ERROR);
    expect(describeSubmitError("string throw")).toBe(GENERIC_SUBMIT_ERROR);
  });
});
