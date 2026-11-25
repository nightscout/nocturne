import { describe, expect, it } from "vitest";
import { httpsHref, maskedSourceUrl } from "./url";

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

describe("httpsHref", () => {
  it("keeps an absolute https URL", () => {
    expect(httpsHref("https://github.com/octo/my-aaps")).toBe(
      "https://github.com/octo/my-aaps"
    );
  });

  it("drops every other scheme, a relative address and nothing", () => {
    expect(httpsHref("javascript:alert(1)")).toBeUndefined();
    expect(httpsHref("http://github.com/octo")).toBeUndefined();
    expect(httpsHref("data:text/html,hi")).toBeUndefined();
    expect(httpsHref("/settings")).toBeUndefined();
    expect(httpsHref("")).toBeUndefined();
    expect(httpsHref(undefined)).toBeUndefined();
  });
});
