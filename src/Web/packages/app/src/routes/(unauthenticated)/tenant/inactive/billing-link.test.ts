import { describe, it, expect } from "vitest";
import { resolveBillingLink } from "./billing-link";

describe("resolveBillingLink", () => {
  it("links the operator's billing page in redirect mode", () => {
    expect(
      resolveBillingLink({
        accountBilling: {
          mode: "redirect",
          url: "https://nocturne.run/account",
          label: "Manage your subscription",
        },
      })
    ).toEqual({ url: "https://nocturne.run/account", label: "Manage your subscription" });
  });

  it("offers no link in api mode without a portal", () => {
    // The hosted deployment configures api mode against a POST-only issue-intake endpoint, so a
    // link here would send a lapsed customer to a route with no GET.
    expect(
      resolveBillingLink({
        accountBilling: { mode: "api", url: "https://nocturne.run/api/support" },
      })
    ).toBeNull();
  });

  it("links the portal in api mode", () => {
    expect(
      resolveBillingLink({
        accountBilling: { mode: "api", url: "https://nocturne.run/api/support" },
        accountPortal: { url: "https://nocturne.run/account", label: "Manage your subscription" },
      })
    ).toEqual({ url: "https://nocturne.run/account", label: "Manage your subscription" });
  });

  it("prefers the portal over a redirect billing channel", () => {
    expect(
      resolveBillingLink({
        accountBilling: { mode: "redirect", url: "https://example.test/issues", label: "Issues" },
        accountPortal: { url: "https://example.test/portal", label: "Portal" },
      })
    ).toEqual({ url: "https://example.test/portal", label: "Portal" });
  });

  it("falls through a portal with no url", () => {
    expect(
      resolveBillingLink({
        accountBilling: { mode: "redirect", url: "https://example.test/account" },
        accountPortal: { url: "" },
      })
    ).toEqual({ url: "https://example.test/account", label: null });
  });

  it("offers no link when the operator configured none", () => {
    expect(resolveBillingLink(null)).toBeNull();
    expect(resolveBillingLink(undefined)).toBeNull();
    expect(resolveBillingLink({})).toBeNull();
    expect(resolveBillingLink({ accountBilling: { mode: "redirect", url: "" } })).toBeNull();
  });

  it("carries a redirect with no label, which the page names itself", () => {
    expect(
      resolveBillingLink({ accountBilling: { mode: "redirect", url: "https://example.test" } })
    ).toEqual({ url: "https://example.test", label: null });
  });

  it("carries a portal with no label, which the page names itself", () => {
    expect(
      resolveBillingLink({ accountPortal: { url: "https://example.test/portal" } })
    ).toEqual({ url: "https://example.test/portal", label: null });
  });
});
