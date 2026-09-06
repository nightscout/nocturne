import { describe, it, expect } from "vitest";
import {
  error,
  fail,
  invalid,
  isActionFailure,
  isHttpError,
  isRedirect,
  isValidationError,
  redirect,
} from "@sveltejs/kit";

/**
 * Only `*.svelte.test.ts` runs under vitest.browser.config.ts, which is where
 * the `@sveltejs/kit` alias to this directory's stub applies — under any other
 * config the imports above resolve to the real framework and prove nothing
 * about the stub.
 */
function thrownBy(fn: () => never): any {
  try {
    fn();
  } catch (e) {
    return e;
  }
  throw new Error("expected the call to throw");
}

describe("@sveltejs/kit stub", () => {
  it("throws an error that is not an Error", () => {
    const thrown = thrownBy(() => error(404, "Not found"));

    expect(thrown).not.toBeInstanceOf(Error);
    expect(isHttpError(thrown)).toBe(true);
    expect(isHttpError(thrown, 404)).toBe(true);
    expect(isHttpError(thrown, 500)).toBe(false);
  });

  it("wraps a string body as the framework does", () => {
    const thrown = thrownBy(() => error(404, "Not found"));

    expect(thrown.status).toBe(404);
    expect(thrown.body).toEqual({ message: "Not found" });
  });

  it("passes an object body through untouched", () => {
    const thrown = thrownBy(() => error(400, { message: "Bad", details: "d" }));

    expect(thrown.body).toEqual({ message: "Bad", details: "d" });
  });

  it("names the status when there is no body", () => {
    const thrown = thrownBy(() => error(500));

    expect(thrown.body).toEqual({ message: "Error: 500" });
  });

  it("throws a redirect that is not an Error", () => {
    const thrown = thrownBy(() => redirect(303, "/login"));

    expect(thrown).not.toBeInstanceOf(Error);
    expect(isRedirect(thrown)).toBe(true);
    expect(isHttpError(thrown)).toBe(false);
    expect(thrown.status).toBe(303);
    expect(thrown.location).toBe("/login");
  });

  it("throws a validation error carrying its issues", () => {
    const thrown = thrownBy(() => invalid("too short", { message: "taken" }));

    expect(isValidationError(thrown)).toBe(true);
    expect(thrown.issues).toEqual([
      { message: "too short" },
      { message: "taken" },
    ]);
  });

  it("returns a failure the framework's guard recognises", () => {
    const failure = fail(400, { field: "name" });

    expect(isActionFailure(failure)).toBe(true);
    expect(failure.status).toBe(400);
    expect(failure.data).toEqual({ field: "name" });
  });
});
