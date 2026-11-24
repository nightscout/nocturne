import { describe, it, expect } from "vitest";
import { TotpClient } from "@nocturne/api-client";
import { classifyAuthenticatorError } from "./authenticator-error";

/** What the generated client throws for a login the API answered with `status` and `body`. */
async function loginRefusedWith(status: number, body: unknown): Promise<unknown> {
  const http = {
    fetch: async () =>
      new Response(JSON.stringify(body), {
        status,
        headers: { "content-type": "application/json" },
      }),
  };
  const client = new TotpClient("http://api.test", http);
  try {
    await client.login({ stepUpToken: "token", code: "123456", trustDevice: false });
  } catch (err) {
    return err;
  }
  throw new Error(`login resolved for status ${status}`);
}

describe("classifyAuthenticatorError over what the generated client throws", () => {
  it("recognises the rate limiter's own body", async () => {
    const thrown = await loginRefusedWith(429, {
      type: "https://tools.ietf.org/html/rfc6585#section-4",
      title: "Too Many Requests",
      status: 429,
      detail: "Too many requests. Please try again later.",
      code: "rate_limit_exceeded",
      error: "rate_limit_exceeded",
      error_description: "Too many requests. Please try again later.",
    });
    expect(classifyAuthenticatorError(thrown)).toBe("rate-limited");
  });

  it("recognises an account that this address does not admit", async () => {
    const thrown = await loginRefusedWith(403, {
      type: "https://tools.ietf.org/html/rfc9110#section-15.5.4",
      title: "Forbidden",
      status: 403,
      detail: "not_a_member",
    });
    expect(classifyAuthenticatorError(thrown)).toBe("not-a-member");
  });

  it("treats the API's refusal as a code that was not accepted", async () => {
    const thrown = await loginRefusedWith(400, {
      type: "https://tools.ietf.org/html/rfc9110#section-15.5.1",
      title: "Bad Request",
      status: 400,
      detail: "That code wasn't accepted. Please sign in again.",
    });
    expect(classifyAuthenticatorError(thrown)).toBe("rejected");
  });
});

describe("classifyAuthenticatorError", () => {
  it("treats a transport failure as a code that was not accepted", () => {
    expect(classifyAuthenticatorError(new Error("fetch failed"))).toBe("rejected");
    expect(classifyAuthenticatorError(null)).toBe("rejected");
  });
});
