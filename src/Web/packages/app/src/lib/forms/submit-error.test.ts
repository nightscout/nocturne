import { describe, it, expect } from "vitest";
import { transformWithEsbuild } from "vite";
import { error } from "@sveltejs/kit";
import config from "../../../../../remote-codegen.config";
import { parseErrorBody } from "$lib/api/error-body";
import {
  describeSubmitError,
  GENERIC_SUBMIT_ERROR,
  RATE_LIMITED_ERROR,
} from "./submit-error";

/**
 * What a 403 looks like by the time a page sees it: the thrown value put
 * through the generated client's own 403 arm, compiled from the codegen config
 * the build reads, so the body is whichever field that arm picked.
 *
 * The arm is spliced into a generated file, so it reaches the helpers that file
 * imports; they are passed in here for the same reason.
 */
type ForbidArm = (
  err: unknown,
  error: typeof import("@sveltejs/kit").error,
  parseErrorBody: typeof import("$lib/api/error-body").parseErrorBody
) => never;

function compileArm(source: string): ForbidArm {
  return new Function(`return ${source}`)();
}

async function crossThe403Arm(thrown: unknown): Promise<unknown> {
  const compiled = await transformWithEsbuild(
    `(err, error, parseErrorBody) => { ${config.errorHandling.on403}; }`,
    "on403.ts",
    { loader: "ts" }
  );

  const forbid = compileArm(compiled.code.trim().replace(/;$/, ""));

  try {
    forbid(thrown, error, parseErrorBody);
  } catch (crossed) {
    return crossed;
  }

  throw new Error("the 403 arm returned without throwing");
}

/** The two messages the generated client puts on an `ApiException` itself. */
const UNDECLARED_STATUS_MESSAGE = "An unexpected server error occurred.";
const DECLARED_STATUS_MESSAGE = "A server side error occurred.";

/** NSwag's ApiException, as a bare `ForbidResult` arrives. */
function nswagApiException(status: number, message: string, response = "") {
  return Object.assign(new Error(message), {
    status,
    response,
    result: null,
  });
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

  it("shows a refusal the server worded on a status the operation never declared", async () => {
    // NSwag parses an error body only for a declared status; otherwise it throws
    // an ApiException whose message is its own boilerplate and leaves the body
    // as raw text. The reason exists only there, so an arm that reads the
    // exception alone reports the boilerplate no matter how it orders its reads.
    const crossed = await crossThe403Arm(
      nswagApiException(
        403,
        UNDECLARED_STATUS_MESSAGE,
        JSON.stringify(problemDetails(403, NEEDS_SCOPE))
      )
    );

    expect(describeSubmitError(crossed, "You can't change this setting.")).toBe(
      NEEDS_SCOPE
    );
  });

  it("keeps the caller's wording when the undeclared body is not JSON", async () => {
    const crossed = await crossThe403Arm(
      nswagApiException(403, UNDECLARED_STATUS_MESSAGE, "<html>403</html>")
    );

    expect(describeSubmitError(crossed, "You can't change this setting.")).toBe(
      "You can't change this setting."
    );
  });

  it("tolerates null and non-objects", () => {
    expect(describeSubmitError(null)).toBe(GENERIC_SUBMIT_ERROR);
    expect(describeSubmitError("string throw")).toBe(GENERIC_SUBMIT_ERROR);
  });
});
