import { describe, it, expect } from "vitest";
import {
  isLastUsed,
  parseLastSignIn,
  withLastUsedFirst,
} from "./last-sign-in";

/**
 * The wire values here are the ones `SessionCookieExtensions.SetLastSignInCookie` writes. Both
 * sides are pinned: `SetLastSignInCookie_names_the_provider_the_login_form_keys_its_buttons_by`
 * asserts the same shape from the API's end.
 */
const PROVIDER_ID = "2f6a4c9e-1d3b-4a58-9f27-8c0b5e6d1a44";

describe("parseLastSignIn", () => {
  it("reads a method with no provider", () => {
    expect(parseLastSignIn("passkey")).toEqual({
      method: "passkey",
      providerId: null,
    });
  });

  it("reads the provider an identity-provider sign-in names", () => {
    expect(parseLastSignIn(`oidc:${PROVIDER_ID}`)).toEqual({
      method: "oidc",
      providerId: PROVIDER_ID,
    });
  });

  it.each([
    ["no cookie", undefined],
    ["an empty cookie", ""],
    ["a method this version does not know", "smartcard"],
    ["a method this version does not know, with a provider", "saml:abc"],
    ["a value that is only a separator", ":"],
  ])("treats %s as no hint at all", (_case, value) => {
    expect(parseLastSignIn(value)).toBeNull();
  });

  it("keeps a provider id that contains a colon whole", () => {
    // The writer joins on the first colon only, so an id with one must not be truncated.
    expect(parseLastSignIn("oidc:urn:example:idp")).toEqual({
      method: "oidc",
      providerId: "urn:example:idp",
    });
  });

  it("reports no provider when the hint names none", () => {
    expect(parseLastSignIn("oidc:")).toEqual({
      method: "oidc",
      providerId: null,
    });
  });
});

describe("isLastUsed", () => {
  it("matches only the provider the hint names", () => {
    const hint = parseLastSignIn(`oidc:${PROVIDER_ID}`);

    expect(isLastUsed(hint, "oidc", PROVIDER_ID)).toBe(true);
    expect(isLastUsed(hint, "oidc", "0000ffff-0000-0000-0000-000000000000")).toBe(
      false
    );
  });

  it("matches a provider id whatever case it is written in", () => {
    // The API writes a lowercase GUID; nothing guarantees the provider list agrees.
    expect(
      isLastUsed(parseLastSignIn(`oidc:${PROVIDER_ID}`), "oidc", PROVIDER_ID.toUpperCase())
    ).toBe(true);
  });

  it("does not match a passkey hint to a provider button", () => {
    const hint = parseLastSignIn("passkey");

    expect(isLastUsed(hint, "passkey")).toBe(true);
    expect(isLastUsed(hint, "oidc", PROVIDER_ID)).toBe(false);
  });

  it("matches nothing without a hint", () => {
    expect(isLastUsed(null, "passkey")).toBe(false);
    expect(isLastUsed(null, "oidc", PROVIDER_ID)).toBe(false);
  });

  it("does not match a provider-less hint to a provider button", () => {
    expect(isLastUsed(parseLastSignIn("oidc"), "oidc", PROVIDER_ID)).toBe(false);
  });
});

describe("withLastUsedFirst", () => {
  const providers = [
    { id: "aaaaaaaa-0000-0000-0000-000000000000", name: "Alpha" },
    { id: PROVIDER_ID, name: "Beta" },
    { id: "cccccccc-0000-0000-0000-000000000000", name: "Gamma" },
  ];

  it("moves the last-used provider to the front", () => {
    const ordered = withLastUsedFirst(
      providers,
      parseLastSignIn(`oidc:${PROVIDER_ID}`)
    );

    expect(ordered.map((p) => p.name)).toEqual(["Beta", "Alpha", "Gamma"]);
  });

  it("leaves the operator's order alone when nothing matches", () => {
    for (const hint of [null, parseLastSignIn("passkey"), parseLastSignIn("oidc:nope")]) {
      expect(withLastUsedFirst(providers, hint).map((p) => p.name)).toEqual([
        "Alpha",
        "Beta",
        "Gamma",
      ]);
    }
  });

  it("does not mutate the list it was given", () => {
    withLastUsedFirst(providers, parseLastSignIn(`oidc:${PROVIDER_ID}`));

    expect(providers.map((p) => p.name)).toEqual(["Alpha", "Beta", "Gamma"]);
  });
});
