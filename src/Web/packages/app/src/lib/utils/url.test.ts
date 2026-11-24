import { describe, expect, it } from "vitest";
import { maskedSourceUrl } from "./url";

describe("maskedSourceUrl", () => {
  it("cuts a token in the path and drops the scheme", () => {
    expect(maskedSourceUrl("https://ns.example.com/AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI/api")).toBe(
      "ns.example.com/AGYL…/api"
    );
  });

  it("keeps a short path and drops a trailing slash", () => {
    expect(maskedSourceUrl("https://ns.example.com/")).toBe("ns.example.com");
    expect(maskedSourceUrl("http://ns.example.com:1337/api/v1")).toBe("ns.example.com:1337/api/v1");
  });

  it("returns a value that is not a URL unchanged", () => {
    expect(maskedSourceUrl("MongoDB: nightscout")).toBe("MongoDB: nightscout");
  });
});
