import { describe, it, expect } from "vitest";
import { readHandoffExchange } from "./handoff-link";

describe("readHandoffExchange", () => {
  it("carries the code and the destination the link asked for", () => {
    expect(
      readHandoffExchange(
        new URL(
          "https://acme.nocturne.run/auth/handoff?code=abc&returnUrl=/reports"
        )
      )
    ).toEqual({ code: "abc", returnUrl: "/reports" });
  });

  it("carries a code with no destination, which the API answers with its own default", () => {
    expect(
      readHandoffExchange(
        new URL("https://acme.nocturne.run/auth/handoff?code=abc")
      )
    ).toEqual({
      code: "abc",
    });
  });

  it("passes an off-site destination through for the API to reject", () => {
    // Deciding this in the browser would put a second open-redirect rule next to the one the
    // API applies, and only the API's decides where the visitor actually lands.
    expect(
      readHandoffExchange(
        new URL(
          "https://acme.nocturne.run/auth/handoff?code=abc&returnUrl=https://evil.example"
        )
      )
    ).toEqual({ code: "abc", returnUrl: "https://evil.example" });
  });

  it("has nothing to exchange when the link carries no code", () => {
    expect(
      readHandoffExchange(new URL("https://acme.nocturne.run/auth/handoff"))
    ).toBeNull();
    expect(
      readHandoffExchange(
        new URL("https://acme.nocturne.run/auth/handoff?code=")
      )
    ).toBeNull();
    expect(
      readHandoffExchange(
        new URL("https://acme.nocturne.run/auth/handoff?returnUrl=/reports")
      )
    ).toBeNull();
  });
});
