import { describe, it, expect, vi, afterEach } from "vitest";
import { transformWithEsbuild } from "vite";
import { error } from "@sveltejs/kit";
import config from "../../../../../remote-codegen.config";
import { parseCode, parseErrorBody } from "#lib/api/error-body.js";
import {
  describeRemoteError,
  describeSubmitError,
  errorIssues,
  errorMessage,
  isSignInRequired,
  PERMISSION_GATED_MUTATION,
  permissionGatedMutationError,
} from "./submit-error";
import {
  genericSubmitError,
  missingItemError,
  missingScopeError,
  rateLimitedError,
  signInRequiredError,
} from "./submit-messages.svelte";
import { confirmIdentity } from "#lib/components/auth/confirm-identity.svelte.js";
import { remoteErrorMessage } from "#lib/api/remote-error.js";

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
  parseErrorBody: typeof import("#lib/api/error-body.js").parseErrorBody,
  parseCode: typeof import("#lib/api/error-body.js").parseCode
) => never;

function compileArm(source: string): ForbidArm {
  return new Function(`return ${source}`)();
}

async function crossThe403Arm(thrown: unknown): Promise<unknown> {
  const compiled = await transformWithEsbuild(
    `(err, error, parseErrorBody, parseCode) => { ${config.errorHandling.on403}; }`,
    "on403.ts",
    { loader: "ts" }
  );

  const forbid = compileArm(compiled.code.trim().replace(/;$/, ""));

  try {
    forbid(thrown, error, parseErrorBody, parseCode);
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

describe("a thrown HttpError", () => {
  it("carries its reason on body.message and nowhere else", () => {
    const thrown = httpError(500, "We couldn't load your settings.");

    expect(thrown).not.toBeInstanceOf(Error);
    expect(thrown).not.toHaveProperty("message");
    expect(errorMessage(thrown)).toBe("We couldn't load your settings.");
  });
});

describe("a query's error", () => {
  it("is read unwrapped, as SvelteKit 3 hands it over", () => {
    const error = { status: 400, message: "Date range must not exceed 365 days.", issues: undefined };

    expect(errorMessage(error)).toBe("Date range must not exceed 365 days.");
  });

  it("is not confused with an Error that happens to carry a status", () => {
    const thrown = Object.assign(new Error("fetch failed"), { status: 500 });

    expect(errorMessage(thrown)).toBeUndefined();
  });
});

describe("errorIssues", () => {
  const issues = [
    { scope: "condition", path: "root", reason: "list_empty", field: null },
  ];

  it("reads a well-formed issue set off the body", () => {
    expect(errorIssues({ status: 400, body: { issues } })).toEqual(issues);
  });

  it("drops a set holding anything that is not a validation issue", () => {
    expect(
      errorIssues({ status: 400, body: { issues: [{ reason: "x" }, "boom"] } })
    ).toBeUndefined();
  });

  it("answers undefined without a body or a set", () => {
    expect(errorIssues(new Error("offline"))).toBeUndefined();
    expect(errorIssues({ status: 400, body: {} })).toBeUndefined();
    expect(errorIssues({ status: 400, body: { issues: "boom" } })).toBeUndefined();
  });
});

describe("describeSubmitError", () => {
  it("uses the handler's message for a 4xx", () => {
    const err = { status: 400, body: { message: "Diabetes type is required" } };
    expect(describeSubmitError(err)).toBe("Diabetes type is required");
  });

  it("hides 5xx detail behind the generic message", () => {
    const err = { status: 500, body: { message: "NullReferenceException" } };
    expect(describeSubmitError(err)).toBe(genericSubmitError());
  });

  it("falls back for a plain thrown Error", () => {
    expect(describeSubmitError(new Error("fetch failed"))).toBe(
      genericSubmitError()
    );
  });

  it("falls back when the 4xx body has no message", () => {
    expect(describeSubmitError({ status: 409, body: {} })).toBe(
      genericSubmitError()
    );
    expect(describeSubmitError({ status: 409, body: { message: "  " } })).toBe(
      genericSubmitError()
    );
  });

  it("reports a throttled attempt as throttled, not as the caller's fallback", () => {
    expect(
      describeSubmitError({ status: 429 }, "This invite link is invalid.")
    ).toBe(rateLimitedError());
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
    // The reason exists only as raw text on `response`; see `#lib/api/error-body.js`.
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
    expect(describeSubmitError(null)).toBe(genericSubmitError());
    expect(describeSubmitError("string throw")).toBe(genericSubmitError());
  });
});

describe("a sign_in_required refusal", () => {
  const handedOffRefusal = {
    ...problemDetails(403, "Sign in to do this. This session was opened from a sign-in link."),
    code: "sign_in_required",
  };

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("asks a writer to sign in, ahead of its fallback and of a sentence it has for the code", async () => {
    const crossed = await crossThe403Arm(handedOffRefusal);

    expect(
      describeSubmitError(crossed, "Could not create the token.", { sign_in_required: "Other." })
    ).toBe(signInRequiredError());
  });

  it("tells a reader to sign in", async () => {
    const crossed = await crossThe403Arm(handedOffRefusal);

    expect(remoteErrorMessage(crossed, "Could not load your connections.")).toBe(
      signInRequiredError()
    );
  });

  // Callers format inside `$derived` (the error page, the join page), where a state write throws,
  // so describing the refusal opens nothing; the refused write opens the prompt itself.
  it("opens nothing while it is described, for a writer or a reader", async () => {
    const prompt = vi.spyOn(confirmIdentity, "request").mockResolvedValue(false);
    const crossed = await crossThe403Arm(handedOffRefusal);

    describeSubmitError(crossed, "Could not create the token.");
    remoteErrorMessage(crossed, "Could not load your connections.");

    expect(prompt).not.toHaveBeenCalled();
    expect(confirmIdentity.open).toBe(false);
  });

  it("is recognised as this refusal, and no other 403 is", async () => {
    expect(isSignInRequired(await crossThe403Arm(handedOffRefusal))).toBe(true);
    expect(
      isSignInRequired(
        await crossThe403Arm({ ...problemDetails(403, "No."), code: "missing_scope" })
      )
    ).toBe(false);
    expect(isSignInRequired({ status: 401, body: { code: "sign_in_required" } })).toBe(false);
  });
});

describe("a missing_scope refusal", () => {
  const scopeRefusal = {
    ...problemDetails(403, "This operation requires the 'tenant.settings' scope."),
    code: "missing_scope",
    scopes: ["tenant.settings"],
  };

  it("answers an ordinary caller with the permission sentence, which names no scope, ahead of its fallback", async () => {
    const crossed = await crossThe403Arm(scopeRefusal);
    const shown = describeSubmitError(crossed, "Could not save the profile.");

    expect(shown).toBe(missingScopeError());
    expect(shown).not.toContain("tenant.settings");
  });

  it("reads the same when the refusal reached NSwag unparsed", async () => {
    const crossed = await crossThe403Arm(
      nswagApiException(403, UNDECLARED_STATUS_MESSAGE, JSON.stringify(scopeRefusal))
    );

    expect(describeSubmitError(crossed, "Could not save the profile.")).toBe(
      missingScopeError()
    );
  });

  it("leaves a permission-gated caller its own refusal sentence", async () => {
    const crossed = await crossThe403Arm(scopeRefusal);

    expect(
      permissionGatedMutationError(
        crossed,
        "Changing alerts requires the alerts.readwrite permission."
      )
    ).toBe("Changing alerts requires the alerts.readwrite permission.");
  });

  it("leaves a 403 with another code to the server's sentence", async () => {
    const crossed = await crossThe403Arm({
      ...problemDetails(403, "Demo accounts cannot do this."),
      code: "demo_subject",
    });

    expect(describeSubmitError(crossed, "You can't change this setting.")).toBe(
      "Demo accounts cannot do this."
    );
  });

  it("lets a call site's own sentence for the code win", async () => {
    const crossed = await crossThe403Arm(scopeRefusal);

    expect(
      describeSubmitError(crossed, "Fallback.", { missing_scope: "Ask an owner." })
    ).toBe("Ask an owner.");
  });

  it("lets a call site's own sentence for the code win over a permission-gated caller's sentence", async () => {
    const crossed = await crossThe403Arm(scopeRefusal);

    expect(
      describeRemoteError(
        crossed,
        "Changing alerts requires the alerts.readwrite permission.",
        PERMISSION_GATED_MUTATION,
        { missing_scope: "Ask an owner." }
      )
    ).toBe("Ask an owner.");
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

    expect(crossed).toHaveProperty("body.message", NEEDS_SCOPE);
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
      missingItemError()
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

  /**
   * SvelteKit answers a non-2xx from `/_app/remote/*` with a body of its own,
   * and the generated client answers an undeclared status with one of two of
   * its own — at any status, so a bare `BadRequest()` reaches a 4xx arm
   * carrying one. Neither names a cause, so a reader gains nothing by showing
   * them and the suppression cannot be scoped to one arm.
   */
  it("agrees on a body the client wrote rather than carried, at every status", () => {
    for (const status of [400, 409, 500, 502]) {
      for (const message of [
        "Failed to execute remote function",
        "An unexpected server error occurred.",
        "A server side error occurred.",
        `Request failed (${status})`,
      ]) {
        const synthesized = { status, body: { message } };

        expect(describeSubmitError(synthesized, WRITE_FALLBACK)).toBe(
          WRITE_FALLBACK
        );
        expect(remoteErrorMessage(synthesized, READ_FALLBACK)).toBe(
          READ_FALLBACK
        );
      }
    }
  });

  it("still carries a server sentence that only mentions a failed request", () => {
    const worded = {
      status: 400,
      body: { message: "Request failed (400): the device code has expired." },
    };

    expect(describeSubmitError(worded, WRITE_FALLBACK)).toBe(
      "Request failed (400): the device code has expired."
    );
  });

  /**
   * Suppression matches a closed set of client-written strings, so what a
   * server wrote about a rejected write — the reason retrying unchanged cannot
   * fix — still reaches the person who has to change it.
   */
  it("still carries a 4xx reason the server worded, flattened ModelState included", () => {
    const validation = {
      status: 400,
      body: { message: "units: Insulin units must be greater than zero." },
    };

    expect(describeSubmitError(validation, WRITE_FALLBACK)).toBe(
      "units: Insulin units must be greater than zero."
    );
    expect(remoteErrorMessage(validation, READ_FALLBACK)).toBe(
      "units: Insulin units must be greater than zero."
    );
  });

  it("agrees on a throttled attempt and on a 4xx the server worded", () => {
    expect(describeSubmitError({ status: 429 }, WRITE_FALLBACK)).toBe(
      rateLimitedError()
    );
    expect(remoteErrorMessage({ status: 429 }, READ_FALLBACK)).toBe(
      rateLimitedError()
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
      missingItemError()
    );
  });

  it("does not blame the permission for a server fault", () => {
    const answer = describe_({
      status: 500,
      body: { message: "npgsql: connection reset" },
    });

    expect(answer).toBe(genericSubmitError());
    expect(answer).not.toBe(NEEDS_ALERTS);
  });

  it("keeps the server's internal text out of it either way", () => {
    expect(
      describe_({ status: 500, body: { message: "npgsql: connection reset" } })
    ).not.toContain("npgsql");
  });

  it("does not blame the permission for a request that never got an answer", () => {
    expect(describe_(new Error("fetch failed"))).toBe(genericSubmitError());
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

describe("a problem code the caller has a sentence for", () => {
  const KNOWN = { role_name_taken: "A role with this name already exists." };
  const FALLBACK = "Failed to create role.";

  it.each([400, 404, 409])("answers a %i with the sentence for its code", (status) => {
    const err = { status, body: { message: "Server text", code: "role_name_taken" } };

    expect(describeSubmitError(err, FALLBACK, KNOWN)).toBe(KNOWN.role_name_taken);
  });

  it("answers a query's own error the same way", () => {
    const err = { status: 409, message: "Server text", code: "role_name_taken" };

    expect(describeSubmitError(err, FALLBACK, KNOWN)).toBe(KNOWN.role_name_taken);
  });

  it("leaves the server's text for a code the caller does not know", () => {
    const err = { status: 409, body: { message: "Server text", code: "other_code" } };

    expect(describeSubmitError(err, FALLBACK, KNOWN)).toBe("Server text");
  });

  it("leaves the server's text when the rejection has no code", () => {
    const err = { status: 409, body: { message: "Server text" } };

    expect(describeSubmitError(err, FALLBACK, KNOWN)).toBe("Server text");
  });

  it("ignores a code when the caller passes no map", () => {
    const err = { status: 409, body: { message: "Server text", code: "role_name_taken" } };

    expect(describeSubmitError(err, FALLBACK)).toBe("Server text");
  });

  it("ignores a malformed code", () => {
    const err = { status: 409, body: { message: "Server text", code: "Role Name Taken" } };

    expect(describeSubmitError(err, FALLBACK, { "Role Name Taken": "No." })).toBe("Server text");
  });

  it("does not mistake an inherited property for a known code", () => {
    const err = { status: 409, body: { message: "Server text", code: "constructor" } };

    expect(describeSubmitError(err, FALLBACK, KNOWN)).toBe("Server text");
  });

  it("still answers a 429 as throttled", () => {
    const err = { status: 429, body: { message: "Too many requests", code: "role_name_taken" } };

    expect(describeSubmitError(err, FALLBACK, KNOWN)).toBe(rateLimitedError());
  });
});
