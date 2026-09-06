import { describe, it, expect } from "vitest";
import { MISSING_ITEM_ERROR, RATE_LIMITED_ERROR } from "../forms/submit-error";
import { remoteErrorMessage } from "./remote-error";

const NEEDS_ALERTS_READWRITE =
  "Changing alerts requires the alerts.readwrite permission.";

describe("remoteErrorMessage", () => {
  it("answers a scope refusal with the permission the caller named", () => {
    expect(
      remoteErrorMessage({ status: 403, body: { message: "Forbidden" } }, NEEDS_ALERTS_READWRITE)
    ).toBe(NEEDS_ALERTS_READWRITE);
  });

  it("keeps naming the permission even when the 403 body reads like a sentence", () => {
    expect(
      remoteErrorMessage(
        {
          status: 403,
          body: { message: "This operation requires the 'alerts.write' scope." },
        },
        NEEDS_ALERTS_READWRITE
      )
    ).toBe(NEEDS_ALERTS_READWRITE);
  });

  it("answers a throttled read as throttled", () => {
    expect(remoteErrorMessage({ status: 429 }, NEEDS_ALERTS_READWRITE)).toBe(
      RATE_LIMITED_ERROR
    );
  });

  it("answers a stale id as a missing item, not a missing permission", () => {
    expect(
      remoteErrorMessage({ status: 404, body: { message: "Not found" } }, NEEDS_ALERTS_READWRITE)
    ).toBe(MISSING_ITEM_ERROR);
  });

  it("forwards the reason a rejected read carried", () => {
    expect(
      remoteErrorMessage(
        { status: 400, body: { message: "That date range is too wide." } },
        NEEDS_ALERTS_READWRITE
      )
    ).toBe("That date range is too wide.");
  });

  it("forwards a 5xx body, which is the only clue why a panel is empty", () => {
    expect(
      remoteErrorMessage(
        { status: 500, body: { message: "Failed to get alert rules" } },
        NEEDS_ALERTS_READWRITE
      )
    ).toBe("Failed to get alert rules");
  });

  it("falls back for a thrown Error, a bare string and null", () => {
    expect(remoteErrorMessage(new Error("fetch failed"), NEEDS_ALERTS_READWRITE)).toBe(
      NEEDS_ALERTS_READWRITE
    );
    expect(remoteErrorMessage("string throw", NEEDS_ALERTS_READWRITE)).toBe(
      NEEDS_ALERTS_READWRITE
    );
    expect(remoteErrorMessage(null, NEEDS_ALERTS_READWRITE)).toBe(
      NEEDS_ALERTS_READWRITE
    );
  });
});
