import { describe, it, expect } from "vitest";
import { transformWithEsbuild } from "vite";
import { error, isHttpError } from "@sveltejs/kit";
import config from "../../../../../remote-codegen.config";
import { describeSubmitError, RATE_LIMITED_ERROR } from "../forms/submit-error";
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
 * NSwag's ApiException, which is what it throws for a status the operation
 * declares no response type for — the rate limiter's 429 among them. The body
 * arrives as unparsed text on `response`, and the message is NSwag's own.
 */
function nswagApiException(status: number, body: string) {
  return Object.assign(
    new Error(
      `The HTTP status code of the response was not expected (${status}).`
    ),
    { status, response: body, result: null }
  );
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

    expect(JSON.stringify(crossed)).not.toContain("not expected");
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

  it("still flattens a status it does not forward", async () => {
    const crossed = await crossTheBoundary(nswagApiException(503, "unavailable"));

    expect((crossed as { status: number }).status).toBe(500);
    expect(describeSubmitError(crossed, "Couldn't load the invite.")).toBe(
      "Couldn't load the invite."
    );
  });
});
