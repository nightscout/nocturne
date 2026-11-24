import { describe, it, expect } from "vitest";
import { classifyRecoveryError } from "./recovery-error";

describe("classifyRecoveryError", () => {
  it("recognises the rate limiter", () => {
    expect(
      classifyRecoveryError({ status: 429, message: "Too Many Requests" })
    ).toBe("rate-limited");
  });

  it("does not read the rate limiter's body as a refused code", () => {
    // A caller matching on the body rather than the status would call a throttled
    // attempt a wrong code.
    expect(
      classifyRecoveryError({
        type: "https://tools.ietf.org/html/rfc6585#section-4",
        title: "Too Many Requests",
        status: 429,
        detail: "Too many requests. Please try again later.",
        code: "rate_limit_exceeded",
        error: "rate_limit_exceeded",
        error_description: "Too many requests. Please try again later.",
      })
    ).toBe("rate-limited");
  });

  it("treats the API's refusal as a wrong username or code", () => {
    expect(
      classifyRecoveryError({ status: 400, message: "Invalid username or recovery code" })
    ).toBe("rejected");
  });

  it("treats a transport failure as a wrong username or code", () => {
    expect(classifyRecoveryError(new Error("fetch failed"))).toBe("rejected");
    expect(classifyRecoveryError(null)).toBe("rejected");
  });
});
