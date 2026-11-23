import { describe, expect, it } from "vitest";
import {
  readRecoveredMethod,
  recoveredDestination,
  withoutRecoveredMethod,
} from "./recovered-method";

const ORIGIN = "https://acme.nocturne.example";

describe("recovered method address", () => {
  it("names the passkey just added in the destination", () => {
    const url = new URL(recoveredDestination("0b1c"), ORIGIN);

    expect(url.pathname).toBe("/settings/account");
    expect(readRecoveredMethod(url)).toEqual({ passkeyId: "0b1c", identityId: undefined });
  });

  it("reads the identity the API appended to a provider destination", () => {
    const url = new URL(`${recoveredDestination()}&linkedIdentity=77aa`, ORIGIN);

    expect(readRecoveredMethod(url)).toEqual({ passkeyId: undefined, identityId: "77aa" });
  });

  it("does not take an ordinary visit to settings for a recovery", () => {
    expect(readRecoveredMethod(new URL("/settings/account?credentialId=0b1c", ORIGIN))).toBeNull();
  });

  it("strips only the recovery parameters", () => {
    const url = new URL(`${recoveredDestination("0b1c")}&linked=success#top`, ORIGIN);

    expect(withoutRecoveredMethod(url)).toBe("/settings/account?linked=success#top");
  });
});
