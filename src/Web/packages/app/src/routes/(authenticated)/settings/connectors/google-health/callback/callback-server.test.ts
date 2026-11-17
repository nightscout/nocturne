import { isRedirect } from "@sveltejs/kit";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { GET } from "./+server";

const complete = vi.fn();

async function outcome(
  query: string,
  { signedIn = true }: { signedIn?: boolean } = {}
): Promise<string | null> {
  const event = {
    url: new URL(`https://sleepy.nocturne.run/settings/connectors/google-health/callback${query}`),
    setHeaders: vi.fn(),
    locals: {
      isAuthenticated: signedIn,
      user: signedIn ? { subjectId: "subject" } : undefined,
      apiClient: { googleHealth: { completeGoogleHealth: complete } },
    },
  };
  try {
    // The handler only uses the slice of RequestEvent built above.
    await GET(event as unknown as Parameters<typeof GET>[0]);
  } catch (thrown) {
    if (!isRedirect(thrown)) throw thrown;
    expect(thrown.status).toBe(303);
    return new URL(thrown.location, "https://sleepy.nocturne.run").searchParams.get("connection");
  }
  throw new Error("The callback did not redirect.");
}

const problem = (detail: unknown, status = 400) => ({ status, title: "Bad Request", detail });

/** The `ApiException` NSwag throws for a status the operation does not declare. */
const apiException = (status: number, response: string) =>
  Object.assign(new Error("An unexpected server error occurred."), { status, response });

describe("Google Health sign-in callback", () => {
  beforeEach(() => complete.mockReset());

  it("connects when the API accepts the code", async () => {
    complete.mockResolvedValueOnce({});
    expect(await outcome("?code=c&state=s")).toBe("connected");
    expect(complete).toHaveBeenLastCalledWith({ code: "c", state: "s" });
  });

  it.each(["account_mismatch", "expired_signin", "permission_denied", "invalid_callback"])(
    "passes the API's %s code through",
    async (code) => {
      complete.mockRejectedValueOnce(problem(code));
      expect(await outcome("?code=c&state=s")).toBe(code);
    }
  );

  it("passes a rate limit through", async () => {
    complete.mockRejectedValueOnce(problem("rate_limited", 429));
    expect(await outcome("?code=c&state=s")).toBe("rate_limited");
  });

  it("reads the code from a body NSwag left unparsed", async () => {
    complete.mockRejectedValueOnce(
      apiException(409, JSON.stringify({ detail: "account_mismatch" }))
    );
    expect(await outcome("?code=c&state=s")).toBe("account_mismatch");
  });

  it.each([
    problem("<script>alert(1)</script>"),
    problem("access_token=private-token"),
    problem("toString"),
    problem("__proto__"),
    problem({ code: "account_mismatch" }),
    apiException(500, "account_mismatch"),
    new TypeError("fetch failed"),
    undefined,
  ])("answers an unknown or hostile failure with the generic outcome", async (error) => {
    complete.mockRejectedValueOnce(error);
    expect(await outcome("?code=c&state=s")).toBe("failed");
  });

  it("does not complete without a session", async () => {
    expect(await outcome("?code=c&state=s", { signedIn: false })).toBe("no_session");
    expect(complete).not.toHaveBeenCalled();
  });

  it("does not complete when Google reports an error", async () => {
    expect(await outcome("?error=access_denied&state=s")).toBe("provider_denied");
    expect(complete).not.toHaveBeenCalled();
  });

  it("fails without a code or state", async () => {
    expect(await outcome("?state=s")).toBe("failed");
    expect(complete).not.toHaveBeenCalled();
  });
});
