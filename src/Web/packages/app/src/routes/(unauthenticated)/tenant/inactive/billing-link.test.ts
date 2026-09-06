import { describe, it, expect } from "vitest";
import { resolveBillingLink } from "./billing-link";

describe("resolveBillingLink", () => {
  it("links the operator's billing page in redirect mode", () => {
    expect(
      resolveBillingLink({
        mode: "redirect",
        url: "https://nocturne.run/account",
        label: "Manage your subscription",
      })
    ).toEqual({ url: "https://nocturne.run/account", label: "Manage your subscription" });
  });

  it("offers no link in api mode", () => {
    // The hosted deployment configures api mode against a POST-only issue-intake endpoint, so a
    // link here would send a lapsed customer to a route with no GET.
    expect(
      resolveBillingLink({ mode: "api", url: "https://nocturne.run/api/support" })
    ).toBeNull();
  });

  it("offers no link when the operator configured none", () => {
    expect(resolveBillingLink(null)).toBeNull();
    expect(resolveBillingLink(undefined)).toBeNull();
    expect(resolveBillingLink({ mode: "redirect", url: "" })).toBeNull();
  });

  it("carries a redirect with no label, which the page names itself", () => {
    expect(resolveBillingLink({ mode: "redirect", url: "https://example.test" }))
      .toEqual({ url: "https://example.test", label: null });
  });
});
