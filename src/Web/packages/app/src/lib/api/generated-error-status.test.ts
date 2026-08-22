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
 * operation declares a typed error response. `title` is the status phrase, so
 * the only thing here a caller can act on is the status.
 */
function problemDetails(status: number, detail: string) {
  return {
    type: `https://tools.ietf.org/html/rfc9110#status.${status}`,
    title: "Not Found",
    status,
    detail,
  };
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

  it("still flattens a status it does not forward", async () => {
    const crossed = await crossTheBoundary(nswagApiException(503, "unavailable"));

    expect((crossed as { status: number }).status).toBe(500);
    expect(describeSubmitError(crossed, "Couldn't load the invite.")).toBe(
      "Couldn't load the invite."
    );
  });
});
