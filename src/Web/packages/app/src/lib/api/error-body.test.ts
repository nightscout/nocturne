import { describe, it, expect } from "vitest";
import { parseErrorBody } from "./error-body";

/**
 * An `ApiException` as NSwag throws it for a status the operation declares no
 * `ProducesResponseType` for: its own boilerplate on `message`, the server's
 * actual body left as raw text on `response`.
 */
function apiException(response: string, status = 403) {
  return {
    message: "A server side error occurred.",
    status,
    response,
    headers: {},
    result: null,
  };
}

describe("parseErrorBody", () => {
  it("recovers the detail of a curated refusal on an undeclared status", () => {
    const body = parseErrorBody(
      apiException(
        JSON.stringify({
          type: "https://tools.ietf.org/html/rfc9110#section-15.5.4",
          title: "Forbidden",
          status: 403,
          detail: "This share does not include treatment data.",
        })
      )
    );

    expect(body?.detail).toBe("This share does not include treatment data.");
    expect(body?.title).toBe("Forbidden");
  });

  it("recovers the validation map of an undeclared 400", () => {
    const body = parseErrorBody(
      apiException(
        JSON.stringify({ errors: { Label: ["The Label field is required."] } }),
        400
      )
    );

    expect(body?.errors).toEqual({ Label: ["The Label field is required."] });
  });

  it("answers undefined for a body that is not JSON", () => {
    expect(
      parseErrorBody(apiException("<html>502 Bad Gateway</html>"))
    ).toBeUndefined();
  });

  it("answers undefined for an empty body", () => {
    expect(parseErrorBody(apiException(""))).toBeUndefined();
    expect(parseErrorBody(apiException("   "))).toBeUndefined();
  });

  it("answers undefined for JSON that is not an object", () => {
    expect(parseErrorBody(apiException('"just a string"'))).toBeUndefined();
    expect(parseErrorBody(apiException("[1, 2, 3]"))).toBeUndefined();
    expect(parseErrorBody(apiException("null"))).toBeUndefined();
  });

  it("keeps a field the far end sent as the wrong type", () => {
    // `Object.entries("boom")` is four entries, which would reach someone as
    // their validation failure.
    const body = parseErrorBody(
      apiException(JSON.stringify({ detail: 42, title: "", errors: "boom" }))
    );

    expect(body?.detail).toBeUndefined();
    expect(body?.title).toBeUndefined();
    expect(body?.errors).toBeUndefined();
  });

  it("answers undefined for a thrown value carrying no response at all", () => {
    // A status the operation declares is thrown as the parsed body itself, which
    // has nothing left to recover.
    expect(
      parseErrorBody({ status: 409, detail: "Already redeemed." })
    ).toBeUndefined();
    expect(parseErrorBody(new Error("network down"))).toBeUndefined();
    expect(parseErrorBody(undefined)).toBeUndefined();
    expect(parseErrorBody(null)).toBeUndefined();
  });
});
