import { describe, expect, it } from "vitest";
import { httpsHref } from "./url";

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
