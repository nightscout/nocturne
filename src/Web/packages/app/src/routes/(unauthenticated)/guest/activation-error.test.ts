import { describe, it, expect } from "vitest";
import { classifyActivationError } from "./activation-error";

describe("classifyActivationError", () => {
  it("treats the API's typed rejection body as a refused code", () => {
    // The generated client throws the parsed 400 body itself, which carries no
    // status — reading only `status` reported every wrong code as an outage.
    expect(
      classifyActivationError({ expiresAt: null, error: "Invalid or expired code" })
    ).toBe("rejected");
  });

  it("treats a rejection body with a null error as a refused code", () => {
    expect(classifyActivationError({ expiresAt: null, error: null })).toBe(
      "rejected"
    );
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

  it("does not read the rate limiter's body as a refused code", () => {
    expect(
      classifyActivationError({
        status: 429,
        error: "rate_limit_exceeded",
      })
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
});
