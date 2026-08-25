import { describe, it, expect } from "vitest";
import { transformWithEsbuild } from "vite";
import { error, isHttpError } from "@sveltejs/kit";
import config from "../../../../../remote-codegen.config";
import {
  describeSubmitError,
  MISSING_ITEM_ERROR,
  RATE_LIMITED_ERROR,
} from "../forms/submit-error";
import { remoteErrorMessage } from "./remote-error";
import { TotpSetupFailure } from "$api-clients";
import {
  describeTotpSetupError,
  describeTotpSetupStartError,
  TOTP_SETUP_FALLBACK,
  TOTP_SETUP_START_FALLBACK,
} from "../components/account/totp-errors";

/**
 * What a failed API call looks like by the time a page sees it.
 *
 * Every generated remote function ends in the catch block
 * openapi-remote-codegen 0.2.0 writes: the status is read off the thrown value,
 * 401 and 403 get a line each, and everything else falls to
 * {@link config.errorHandling.on500} — which is ours. A status `on500` does not
 * name is flattened to a 500 and never reaches the browser, so a page cannot
 * tell "you were throttled" from "this failed". These run the real `on500`
 * source, compiled the way the build compiles the generated file, over the
 * shape NSwag actually throws — not a hand-built `{ status }`.
 */
async function crossTheBoundary(thrown: unknown): Promise<unknown> {
  const compiled = await transformWithEsbuild(
    `(err, status, error) => { ${config.errorHandling.on500("get invite info")}; }`,
    "on500.ts",
    { loader: "ts" }
  );
  const source = compiled.code.trim().replace(/;$/, "");

  const flatten = new Function(`return ${source}`)() as (
    err: unknown,
    status: unknown,
    error: typeof import("@sveltejs/kit").error
  ) => never;

  try {
    flatten(thrown, (thrown as { status?: number })?.status, error);
  } catch (crossed) {
    return crossed;
  }

  throw new Error("the catch block returned without throwing");
}

/**
 * The two messages the generated client puts on an `ApiException`: the first
 * for a status the operation declares no response type for — the rate limiter's
 * 429 among them — and the second for one it declares but whose body came back
 * empty or unparsed, as a bare `NotFound()` does.
 */
const UNDECLARED_STATUS_MESSAGE = "An unexpected server error occurred.";
const DECLARED_STATUS_MESSAGE = "A server side error occurred.";

/**
 * NSwag's ApiException. The body arrives as unparsed text on `response`, and
 * the message is NSwag's own rather than anything the server wrote.
 */
function nswagApiException(
  status: number,
  body: string,
  message = UNDECLARED_STATUS_MESSAGE
) {
  return Object.assign(new Error(message), {
    status,
    response: body,
    result: null,
  });
}

/**
 * An RFC-7807 body, which NSwag throws as the parsed object itself when the
 * operation declares a typed error response. `title` is the status phrase;
 * `detail` is the sentence saying what went wrong.
 */
function problemDetails(status: number, detail: string, title = "Not Found") {
  return {
    type: `https://tools.ietf.org/html/rfc9110#status.${status}`,
    title,
    status,
    detail,
  };
}

/**
 * A SvelteKit `HttpError`, which is what the refresh of an invalidated query
 * throws from inside the same `try` as the client call. Its reason lives on
 * `body.message` and nowhere else.
 */
function refusal(status: number, message: string): unknown {
  try {
    error(status, message);
  } catch (thrown) {
    return thrown;
  }
}

const RATE_LIMIT_BODY = JSON.stringify({
  error: "rate_limit_exceeded",
  error_description: "Too many requests. Please try again later.",
});

describe("the status a generated remote function lets through", () => {
  it("forwards a 429 rather than flattening it to a 500", async () => {
    const crossed = await crossTheBoundary(nswagApiException(429, RATE_LIMIT_BODY));

    expect(isHttpError(crossed)).toBe(true);
    expect((crossed as { status: number }).status).toBe(429);
  });

  it("keeps NSwag's boilerplate out of the message it carries", async () => {
    const crossed = await crossTheBoundary(nswagApiException(429, RATE_LIMIT_BODY));

    expect(JSON.stringify(crossed)).not.toContain("error occurred");
  });

  it("reads as throttled on the invite page, not as a dead invite", async () => {
    const crossed = await crossTheBoundary(nswagApiException(429, RATE_LIMIT_BODY));

    expect(
      describeSubmitError(
        crossed,
        "This invite link is invalid or has expired."
      )
    ).toBe(RATE_LIMITED_ERROR);
  });

  it("reads as throttled where a scope refusal would name a permission", async () => {
    const crossed = await crossTheBoundary(nswagApiException(429, RATE_LIMIT_BODY));

    expect(remoteErrorMessage(crossed, "You need alerts.readwrite.")).toBe(
      RATE_LIMITED_ERROR
    );
  });

  it("forwards a 404 rather than flattening it to a 500", async () => {
    const crossed = await crossTheBoundary(
      problemDetails(404, "Data source not found: dexcom")
    );

    expect(isHttpError(crossed) && crossed.status).toBe(404);
  });

  it("forwards a 404 whose body is empty, as `NotFound()` sends it", async () => {
    const crossed = await crossTheBoundary(
      nswagApiException(404, "", DECLARED_STATUS_MESSAGE)
    );

    expect(isHttpError(crossed) && crossed.status).toBe(404);
    expect(JSON.stringify(crossed)).not.toContain("error occurred");
  });

  it("leaves a dialog its own wording for a resource that is already gone", async () => {
    const crossed = await crossTheBoundary(
      nswagApiException(404, "", DECLARED_STATUS_MESSAGE)
    );

    expect(
      describeSubmitError(crossed, "This data source is already gone.")
    ).toBe("This data source is already gone.");
  });

  it("does not read as a missing permission when an id is stale", async () => {
    const crossed = await crossTheBoundary(
      nswagApiException(404, "", DECLARED_STATUS_MESSAGE)
    );

    expect(
      remoteErrorMessage(crossed, "Changing alerts requires alerts.readwrite.")
    ).toBe(MISSING_ITEM_ERROR);
  });

  it("keeps a 404 detail that echoes back an id out of what it forwards", async () => {
    const crossed = await crossTheBoundary(
      problemDetails(404, "Body weight record with ID 3f2a not found")
    );

    expect(JSON.stringify(crossed)).not.toContain("3f2a");
  });

  it("says why a conflict happened rather than saying 'Conflict'", async () => {
    const crossed = await crossTheBoundary(
      problemDetails(409, "Cannot revoke an already-redeemed invite", "Conflict")
    );

    expect(isHttpError(crossed) && crossed.status).toBe(409);
    expect(describeSubmitError(crossed, "Couldn't revoke the invite.")).toBe(
      "Cannot revoke an already-redeemed invite"
    );
  });

  it("names the field a validation failure came from, not the summary above it", async () => {
    const crossed = await crossTheBoundary({
      ...problemDetails(
        400,
        "One or more validation errors occurred.",
        "Bad Request"
      ),
      errors: { ids: ["The ids field is required."] },
    });

    expect(describeSubmitError(crossed, "Couldn't save your changes.")).toBe(
      "The ids field is required."
    );
  });

  it("forwards the message when an invalidated query's refresh is what failed", async () => {
    const crossed = await crossTheBoundary(
      refusal(409, "Another device changed this entry.")
    );

    expect(describeSubmitError(crossed, "Couldn't save your changes.")).toBe(
      "Another device changed this entry."
    );
  });

  it("still flattens a status it does not forward", async () => {
    const crossed = await crossTheBoundary(nswagApiException(503, "unavailable"));

    expect((crossed as { status: number }).status).toBe(500);
    expect(describeSubmitError(crossed, "Couldn't load the invite.")).toBe(
      "Couldn't load the invite."
    );
  });
});

/** The settings page calls this with no fallback of its own, so the default is what ships. */
const describeAsThePageDoes = (err: unknown) => describeTotpSetupError(err);

async function wordingFor(detail: string): Promise<string> {
  return describeAsThePageDoes(
    await crossTheBoundary(problemDetails(400, detail, "Bad Request"))
  );
}

describe("a refused authenticator setup", () => {
  it("turns each failure the server can raise into its own wording", async () => {
    const wordings = await Promise.all(
      Object.values(TotpSetupFailure).map(wordingFor)
    );

    expect(wordings).not.toContain(TOTP_SETUP_FALLBACK);
    expect(new Set(wordings).size).toBe(Object.values(TotpSetupFailure).length);
  });

  it("names the expiry rather than the generic refusal", async () => {
    expect(await wordingFor(TotpSetupFailure.ChallengeExpired)).toContain(
      "took too long"
    );
  });

  it("shows no failure value to the user", async () => {
    const wordings = await Promise.all(
      Object.values(TotpSetupFailure).map(wordingFor)
    );

    for (const failure of Object.values(TotpSetupFailure)) {
      expect(wordings.join(" ")).not.toContain(failure);
    }
  });

  it("falls back rather than showing a failure this build does not know", async () => {
    expect(await wordingFor("SomethingAddedLater")).toBe(TOTP_SETUP_FALLBACK);
  });

  /**
   * `Object.prototype` answers to these; a lookup that walked the chain would hand
   * back a function where the page expects a sentence.
   */
  it.each(["toString", "constructor", "hasOwnProperty"])(
    "does not mistake %s for a failure it has copy for",
    async (inherited) => {
      expect(await wordingFor(inherited)).toBe(TOTP_SETUP_FALLBACK);
    }
  );

  it("still says something when the request failed for another reason", async () => {
    const crossed = await crossTheBoundary(nswagApiException(503, "unavailable"));

    expect(describeAsThePageDoes(crossed)).toBe(TOTP_SETUP_FALLBACK);
    expect(TOTP_SETUP_FALLBACK.trim()).not.toBe("");
  });
});

describe("an authenticator setup that was refused before it started", () => {
  it("says which primary factor to add rather than naming a server error", async () => {
    const crossed = await crossTheBoundary(
      problemDetails(400, TotpSetupFailure.NoPrimaryFactor, "Bad Request")
    );

    // What the endpoint sent before it declared a 400 response type.
    expect(describeTotpSetupStartError(crossed)).not.toContain("error occurred");
    expect(describeTotpSetupStartError(crossed)).toContain("passkey");
  });

  it("does not tell someone to check a code they were never asked for", async () => {
    const crossed = await crossTheBoundary(
      problemDetails(400, TotpSetupFailure.NoPrimaryFactor, "Bad Request")
    );

    expect(describeTotpSetupStartError(crossed)).not.toBe(TOTP_SETUP_FALLBACK);
  });

  it("falls back on its own wording, not the verify step's", async () => {
    const crossed = await crossTheBoundary(nswagApiException(503, "unavailable"));

    expect(describeTotpSetupStartError(crossed)).toBe(TOTP_SETUP_START_FALLBACK);
    expect(TOTP_SETUP_START_FALLBACK.trim()).not.toBe("");
  });
});
