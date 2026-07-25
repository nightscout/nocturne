import { describe, it, expect } from "vitest";
import {
  CeremonyOptionsError,
  describePasskeyError,
  parseCeremonyOptions,
} from "./passkey-errors";

describe("parseCeremonyOptions", () => {
  it("parses a JSON options blob", () => {
    expect(parseCeremonyOptions('{"challenge":"abc"}')).toEqual({
      challenge: "abc",
    });
  });

  it("rejects a missing blob instead of throwing a JSON syntax error", () => {
    // JSON.parse(response.options ?? "") produced "Unexpected end of JSON
    // input", which was then shown to the user.
    expect(() => parseCeremonyOptions(undefined)).toThrow(CeremonyOptionsError);
    expect(() => parseCeremonyOptions(null)).toThrow(CeremonyOptionsError);
    expect(() => parseCeremonyOptions("")).toThrow(CeremonyOptionsError);
    expect(() => parseCeremonyOptions("   ")).toThrow(CeremonyOptionsError);
  });

  it("rejects an unparseable blob", () => {
    expect(() => parseCeremonyOptions("{not json")).toThrow(
      CeremonyOptionsError
    );
  });

  it("rejects a non-object blob", () => {
    expect(() => parseCeremonyOptions("42")).toThrow(CeremonyOptionsError);
    expect(() => parseCeremonyOptions("null")).toThrow(CeremonyOptionsError);
  });
});

function domError(name: string): Error {
  const err = new Error("raw browser text that should not be shown");
  err.name = name;
  return err;
}

describe("describePasskeyError", () => {
  it("explains a cancelled or timed-out sign-in prompt", () => {
    const message = describePasskeyError(domError("NotAllowedError"), "login");
    expect(message).toContain("No passkey was used");
    expect(message).toContain("recovery code");
    expect(message).not.toContain("raw browser text");
  });

  it("explains a cancelled registration prompt", () => {
    expect(describePasskeyError(domError("NotAllowedError"), "register")).toContain(
      "No passkey was created"
    );
  });

  it("tells a returning user their device already has a passkey", () => {
    expect(
      describePasskeyError(domError("InvalidStateError"), "register")
    ).toContain("already has a passkey");
  });

  it("does not claim a passkey exists when signing in fails that way", () => {
    expect(describePasskeyError(domError("InvalidStateError"), "login")).toContain(
      "can't be used here"
    );
  });

  it("handles an aborted ceremony", () => {
    expect(describePasskeyError(domError("AbortError"), "login")).toContain(
      "closed before it finished"
    );
  });

  it("handles a blocked origin", () => {
    expect(describePasskeyError(domError("SecurityError"), "login")).toContain(
      "blocked the passkey prompt"
    );
  });

  it("handles a device that can't make a passkey", () => {
    expect(
      describePasskeyError(domError("NotSupportedError"), "register")
    ).toContain("can't be used to create a passkey");
  });

  it("explains a missing options blob without JSON jargon", () => {
    const message = describePasskeyError(
      new CeremonyOptionsError("The server returned no passkey options"),
      "login"
    );
    expect(message).toContain("couldn't start the passkey step");
    expect(message).not.toContain("JSON");
  });

  it("passes through a 4xx message from our own server", () => {
    expect(
      describePasskeyError(
        { status: 400, body: { message: "That username is not registered." } },
        "login"
      )
    ).toBe("That username is not registered.");
  });

  it("hides a 5xx detail behind the fallback", () => {
    expect(
      describePasskeyError(
        { status: 500, body: { message: "NullReferenceException" } },
        "login"
      )
    ).toBe("We couldn't complete that step. Please try again.");
  });

  it("uses the caller's fallback for anything unrecognised", () => {
    expect(
      describePasskeyError(new Error("fetch failed"), "login", "Custom text.")
    ).toBe("Custom text.");
  });

  it("tolerates non-error throws", () => {
    expect(describePasskeyError(null, "login")).toContain("couldn't complete");
    expect(describePasskeyError("boom", "register")).toContain(
      "couldn't complete"
    );
  });
});
