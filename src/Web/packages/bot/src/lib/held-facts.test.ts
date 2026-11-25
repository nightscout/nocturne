import { describe, it, expect } from "vitest";
import { heldFactName, heldReason } from "./held-facts.js";

describe("heldFactName and heldReason", () => {
  it("name what they know and give back what they do not know as sent", () => {
    expect(heldFactName("glucose")).toBe("Glucose readings");
    expect(heldFactName("warp_core")).toBe("warp_core");
    expect(heldReason("stale")).toBe("Too old");
    expect(heldReason("warp_core")).toBe("warp_core");
  });

  it("do not read the object prototype", () => {
    expect(heldFactName("constructor")).toBe("constructor");
    expect(heldFactName("toString")).toBe("toString");
    expect(heldReason("constructor")).toBe("constructor");
    expect(heldReason("__proto__")).toBe("__proto__");
  });

  it("give nothing for nothing", () => {
    expect(heldFactName(undefined)).toBe("");
    expect(heldReason(undefined)).toBe("");
  });
});
