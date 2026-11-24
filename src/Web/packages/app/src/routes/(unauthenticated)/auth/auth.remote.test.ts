import { describe, it, expect, vi, beforeEach } from "vitest";
import { isRedirect, isValidationError } from "@sveltejs/kit";

let login: (body: unknown) => Promise<{ success?: boolean }>;

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({
    locals: { apiClient: { totp: { login: (body: unknown) => login(body) } } },
  }),
  query: (_schema: unknown, fn: unknown) => fn,
  command: (_schema: unknown, fn: unknown) => fn,
  form: (_schema: unknown, fn: unknown) => fn,
}));

vi.spyOn(console, "error").mockImplementation(() => {});

const { signInWithAuthenticator } = await import("./auth.remote");

type Submission = {
  stepUpToken: string;
  code: string;
  trustDevice?: boolean;
  returnUrl?: string;
};

type Handler = (
  data: Submission,
  issue: { code: (message: string) => { message: string } }
) => Promise<unknown>;

const issue = { code: (message: string) => ({ message }) };

const NOT_A_MEMBER =
  "Your account isn't a member here. Go back to sign in, or request membership.";
const RATE_LIMITED = "Too many attempts. Wait a few minutes and try again.";
const NOT_ACCEPTED =
  "The code was not accepted. Type the current code from your app. If this does not work, go back and sign in again.";

/** What the generated client throws for a response the API answered with `status`. */
const apiException = (status: number) =>
  Object.assign(new Error(`The server answered ${status}`), { status });

async function submit(data: Partial<Submission> = {}): Promise<unknown> {
  try {
    return await (signInWithAuthenticator as unknown as Handler)(
      { stepUpToken: "step-up", code: "123456", ...data },
      issue
    );
  } catch (thrown) {
    return thrown;
  }
}

/** The sentence the sign-in form shows beside the code field. */
async function shownFor(status: number): Promise<string> {
  login = () => Promise.reject(apiException(status));

  const thrown = await submit();

  if (!isValidationError(thrown)) {
    throw new Error("the form did not reject with a validation error");
  }
  const { issues } = thrown as unknown as { issues: { message: string }[] };
  return issues[0].message;
}

describe("signing in with an authenticator code", () => {
  beforeEach(() => {
    login = () => Promise.resolve({ success: true });
  });

  it("sends the step-up token, the code and the trust choice to the API", async () => {
    const sent: unknown[] = [];
    login = (body) => {
      sent.push(body);
      return Promise.resolve({ success: true });
    };

    await submit({ trustDevice: true });

    expect(sent).toEqual([
      { stepUpToken: "step-up", code: "123456", trustDevice: true },
    ]);
  });

  it("tells the person a limit turned the attempt away on a 429", async () => {
    expect(await shownFor(429)).toBe(RATE_LIMITED);
  });

  it("tells the person the account is not a member on a 403", async () => {
    expect(await shownFor(403)).toBe(NOT_A_MEMBER);
  });

  it("tells the person the code was not accepted on a 400", async () => {
    expect(await shownFor(400)).toBe(NOT_ACCEPTED);
  });

  it("tells the person the code was not accepted when the API says it failed", async () => {
    login = () => Promise.resolve({ success: false });

    const thrown = await submit();

    expect(isValidationError(thrown)).toBe(true);
    const { issues } = thrown as unknown as { issues: { message: string }[] };
    expect(issues[0].message).toBe(NOT_ACCEPTED);
  });

  it("redirects to the safe return URL once the API accepts the code", async () => {
    const thrown = await submit({ returnUrl: "/reports" });

    expect(isRedirect(thrown)).toBe(true);
    expect(thrown).toMatchObject({ status: 303, location: "/reports" });
  });

  it("redirects to the root when the return URL is not a path on this site", async () => {
    const thrown = await submit({ returnUrl: "//evil.test" });

    expect(isRedirect(thrown)).toBe(true);
    expect(thrown).toMatchObject({ status: 303, location: "/" });
  });
});
