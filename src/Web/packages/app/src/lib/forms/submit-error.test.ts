import { describe, it, expect } from "vitest";
import { transformWithEsbuild } from "vite";
import { error } from "@sveltejs/kit";
import config from "../../../../../remote-codegen.config";
import {
  describeRemoteError,
  describeSubmitError,
  GENERIC_SUBMIT_ERROR,
  MISSING_ITEM_ERROR,
  PERMISSION_GATED_MUTATION,
  RATE_LIMITED_ERROR,
} from "./submit-error";
import { remoteErrorMessage } from "$lib/api/remote-error";

/**
 * What a 403 looks like by the time a page sees it: the thrown value put
 * through the generated client's own 403 arm, compiled from the codegen config
 * the build reads, so the body is whichever field that arm picked.
 */
type ForbidArm = (
  err: unknown,
  error: typeof import("@sveltejs/kit").error
) => never;

function compileArm(source: string): ForbidArm {
  return new Function(`return ${source}`)();
}

async function crossThe403Arm(thrown: unknown): Promise<unknown> {
  const compiled = await transformWithEsbuild(
    `(err, error) => { ${config.errorHandling.on403}; }`,
    "on403.ts",
    { loader: "ts" }
  );

  const forbid = compileArm(compiled.code.trim().replace(/;$/, ""));

  try {
    forbid(thrown, error);
  } catch (crossed) {
    return crossed;
  }

  throw new Error("the 403 arm returned without throwing");
}

/** The two messages the generated client puts on an `ApiException` itself. */
const UNDECLARED_STATUS_MESSAGE = "An unexpected server error occurred.";
const DECLARED_STATUS_MESSAGE = "A server side error occurred.";

/** NSwag's ApiException, as a bare `ForbidResult` arrives. */
function nswagApiException(status: number, message: string) {
  return Object.assign(new Error(message), {
    status,
    response: "",
    result: null,
  });
}

/** A SvelteKit `HttpError`, as an invalidated query's refresh throws it. */
function httpError(status: number, message: string): unknown {
  try {
    error(status, message);
  } catch (thrown) {
    return thrown;
  }
}

/** An RFC-7807 body, thrown as the parsed object when the 403 is declared. */
function problemDetails(status: number, detail: string) {
  return {
    type: `https://tools.ietf.org/html/rfc9110#status.${status}`,
    title: "Forbidden",
    status,
    detail,
  };
}

const NEEDS_SCOPE = "This operation requires the 'activity.write' scope.";

describe("describeSubmitError", () => {
  it("uses the handler's message for a 4xx", () => {
    const err = { status: 400, body: { message: "Diabetes type is required" } };
    expect(describeSubmitError(err)).toBe("Diabetes type is required");
  });

  it("hides 5xx detail behind the generic message", () => {
    const err = { status: 500, body: { message: "NullReferenceException" } };
    expect(describeSubmitError(err)).toBe(GENERIC_SUBMIT_ERROR);
  });

  it("falls back for a plain thrown Error", () => {
    expect(describeSubmitError(new Error("fetch failed"))).toBe(
      GENERIC_SUBMIT_ERROR
    );
  });

  it("falls back when the 4xx body has no message", () => {
    expect(describeSubmitError({ status: 409, body: {} })).toBe(
      GENERIC_SUBMIT_ERROR
    );
    expect(describeSubmitError({ status: 409, body: { message: "  " } })).toBe(
      GENERIC_SUBMIT_ERROR
    );
  });

  it("reports a throttled attempt as throttled, not as the caller's fallback", () => {
    expect(
      describeSubmitError({ status: 429 }, "This invite link is invalid.")
    ).toBe(RATE_LIMITED_ERROR);
  });

  it("uses the caller's fallback", () => {
    expect(describeSubmitError(new Error("x"), "Couldn't save.")).toBe(
      "Couldn't save."
    );
  });

  it("keeps the caller's wording when a bare ForbidResult is all the server sent", async () => {
    const crossed = await crossThe403Arm(
      nswagApiException(403, UNDECLARED_STATUS_MESSAGE)
    );

    expect(describeSubmitError(crossed, "You can't change this setting.")).toBe(
      "You can't change this setting."
    );
  });

  it("keeps the caller's wording for a declared 403 that came back empty", async () => {
    const crossed = await crossThe403Arm(
      nswagApiException(403, DECLARED_STATUS_MESSAGE)
    );

    expect(describeSubmitError(crossed, "You can't change this setting.")).toBe(
      "You can't change this setting."
    );
  });

  it("keeps the caller's wording when the arm falls through to the status phrase", () => {
    expect(
      describeSubmitError(
        { status: 403, body: { message: "Forbidden" } },
        "You can't change this setting."
      )
    ).toBe("You can't change this setting.");
  });

  it("shows a refusal the server worded itself", async () => {
    const crossed = await crossThe403Arm(problemDetails(403, NEEDS_SCOPE));

    expect(describeSubmitError(crossed, "You can't change this setting.")).toBe(
      NEEDS_SCOPE
    );
  });

  it("tolerates null and non-objects", () => {
    expect(describeSubmitError(null)).toBe(GENERIC_SUBMIT_ERROR);
    expect(describeSubmitError("string throw")).toBe(GENERIC_SUBMIT_ERROR);
  });
});

describe("the 403 arm", () => {
  it("forwards the reason when an invalidated query's refresh is what was refused", async () => {
    const crossed = await crossThe403Arm(
      httpError(403, "You can't manage members of this tenant.")
    );

    expect(describeSubmitError(crossed, "You can't change this setting.")).toBe(
      "You can't manage members of this tenant."
    );
  });

  it("prefers the sentence the server wrote to the status phrase beside it", async () => {
    const crossed = await crossThe403Arm(problemDetails(403, NEEDS_SCOPE));

    expect((crossed as { body: { message: string } }).body.message).toBe(
      NEEDS_SCOPE
    );
  });
});

/**
 * The three points on which the reading and writing halves differ. Each row is
 * a decision recorded at `RemoteErrorPolicy`, so collapsing one reddens here.
 */
describe("the two halves of RemoteErrorPolicy", () => {
  const WRITE_FALLBACK = "Failed to delete the alert rule.";
  const READ_FALLBACK =
    "Changing alerts requires the alerts.readwrite permission.";

  it("differs on a 404", () => {
    expect(describeSubmitError({ status: 404 }, WRITE_FALLBACK)).toBe(
      WRITE_FALLBACK
    );
    expect(remoteErrorMessage({ status: 404 }, READ_FALLBACK)).toBe(
      MISSING_ITEM_ERROR
    );
  });

  it("differs on a 403 the server worded itself", () => {
    const refusal = { status: 403, body: { message: NEEDS_SCOPE } };

    expect(describeSubmitError(refusal, WRITE_FALLBACK)).toBe(NEEDS_SCOPE);
    expect(remoteErrorMessage(refusal, READ_FALLBACK)).toBe(READ_FALLBACK);
  });

  it("differs on a body that did not come with a 4xx", () => {
    const fault = {
      status: 500,
      body: { message: "npgsql: connection reset" },
    };

    expect(describeSubmitError(fault, WRITE_FALLBACK)).toBe(WRITE_FALLBACK);
    expect(remoteErrorMessage(fault, READ_FALLBACK)).toBe(
      "npgsql: connection reset"
    );
  });

  it("agrees on a throttled attempt and on a 4xx the server worded", () => {
    expect(describeSubmitError({ status: 429 }, WRITE_FALLBACK)).toBe(
      RATE_LIMITED_ERROR
    );
    expect(remoteErrorMessage({ status: 429 }, READ_FALLBACK)).toBe(
      RATE_LIMITED_ERROR
    );

    const rejected = { status: 409, body: { message: "Already redeemed." } };
    expect(describeSubmitError(rejected, WRITE_FALLBACK)).toBe(
      "Already redeemed."
    );
    expect(remoteErrorMessage(rejected, READ_FALLBACK)).toBe(
      "Already redeemed."
    );
  });
});

describe("a mutation whose fallback names the permission its page is gated on", () => {
  const NEEDS_ALERTS =
    "Changing alerts requires the alerts.readwrite permission.";
  const describe_ = (err: unknown) =>
    describeRemoteError(err, NEEDS_ALERTS, PERMISSION_GATED_MUTATION);

  it("says the rule is gone rather than that the permission is missing", () => {
    expect(describe_({ status: 404, body: { message: "Not found" } })).toBe(
      MISSING_ITEM_ERROR
    );
  });

  it("does not blame the permission for a server fault", () => {
    const answer = describe_({
      status: 500,
      body: { message: "npgsql: connection reset" },
    });

    expect(answer).toBe(GENERIC_SUBMIT_ERROR);
    expect(answer).not.toBe(NEEDS_ALERTS);
  });

  it("keeps the server's internal text out of it either way", () => {
    expect(
      describe_({ status: 500, body: { message: "npgsql: connection reset" } })
    ).not.toContain("npgsql");
  });

  it("does not blame the permission for a request that never got an answer", () => {
    expect(describe_(new Error("fetch failed"))).toBe(GENERIC_SUBMIT_ERROR);
  });

  it("names the permission for the refusal the scope attribute produces", () => {
    expect(describe_({ status: 403, body: { message: "Forbidden" } })).toBe(
      NEEDS_ALERTS
    );
  });

  it("still shows a refusal the server worded itself", () => {
    expect(describe_({ status: 403, body: { message: NEEDS_SCOPE } })).toBe(
      NEEDS_SCOPE
    );
  });
});
