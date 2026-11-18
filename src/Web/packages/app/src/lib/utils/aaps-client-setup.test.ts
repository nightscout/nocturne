import { describe, it, expect } from "vitest";
import { buildAapsClientSetupCode } from "./aaps-client-setup.js";

describe("buildAapsClientSetupCode", () => {
  it("is the set-up code AAPS 4 accepts", () => {
    const code = JSON.parse(buildAapsClientSetupCode("https://me.nocturne.example", "noc_abc"));

    expect(code).toEqual({
      type: "aaps-client-setup",
      v: 1,
      url: "https://me.nocturne.example",
      token: "noc_abc",
    });
  });

  it("drops the trailing slash AAPS would join a path onto", () => {
    const code = JSON.parse(buildAapsClientSetupCode("https://me.nocturne.example/", "noc_abc"));

    expect(code.url).toBe("https://me.nocturne.example");
  });
});
