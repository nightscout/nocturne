import { describe, it, expect } from "vitest";
import { classifyActivationError } from "./activation-error";

describe("classifyActivationError", () => {
  it("treats the API's refusal as a refused code", () => {
    // ProblemDetails carries the status in its own body, so the parsed throw the
    // generated client hands back has one.
    expect(
      classifyActivationError({
        type: "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        title: "Bad Request",
        status: 400,
        detail: "Invalid or expired code",
      })
    ).toBe("rejected");
  });

  it("treats an explicit 400 as a refused code", () => {
    expect(classifyActivationError({ status: 400, message: "Bad Request" })).toBe(
      "rejected"
    );
  });

  it("recognises the rate limiter", () => {
    expect(
      classifyActivationError({ status: 429, message: "Too Many Requests" })
    ).toBe("rate-limited");
  });

  it("reports a server fault as unavailable rather than a bad code", () => {
    expect(classifyActivationError({ status: 500, message: "boom" })).toBe(
      "unavailable"
    );
  });

  it("reports a transport failure as unavailable", () => {
    expect(classifyActivationError(new Error("fetch failed"))).toBe(
      "unavailable"
    );
    expect(classifyActivationError(null)).toBe("unavailable");
    expect(classifyActivationError("boom")).toBe("unavailable");
  });

  it("does not read a status-less body as a refused code", () => {
    // A body with no status is an outage we cannot classify, not a bad code.
    expect(classifyActivationError({ expiresAt: null })).toBe("unavailable");
  });
});
