import { describe, it, expect, vi } from "vitest";
import { offerPasskeyInAutofill } from "./passkey-login";

/**
 * Conditional mediation has to ask the server for a challenge before the visitor has done
 * anything, and a browser that cannot honour it would be left holding a pending WebAuthn request
 * that swallows the explicit sign-in buttons. So the feature detection gates the request, not the
 * other way round.
 */
describe("offerPasskeyInAutofill", () => {
  it("asks for nothing when the browser cannot offer a passkey in autofill", async () => {
    const assertion = vi.fn();

    const result = await offerPasskeyInAutofill(assertion, async () => false);

    expect(assertion).not.toHaveBeenCalled();
    expect(result).toBeNull();
  });

  it("runs the ceremony when the browser can", async () => {
    const assertion = vi.fn(async () => "signed-in");

    const result = await offerPasskeyInAutofill(assertion, async () => true);

    expect(assertion).toHaveBeenCalledOnce();
    expect(result).toBe("signed-in");
  });

  it("lets a failed ceremony reach the caller, which reports nothing to the visitor", async () => {
    const assertion = vi.fn(async () => {
      throw new Error("NotAllowedError");
    });

    await expect(
      offerPasskeyInAutofill(assertion, async () => true)
    ).rejects.toThrow("NotAllowedError");
  });
});
