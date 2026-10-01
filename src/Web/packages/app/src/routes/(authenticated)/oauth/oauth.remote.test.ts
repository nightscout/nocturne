import { describe, it, expect, vi } from "vitest";
import { isValidationError } from "@sveltejs/kit";

let answer: () => Response;

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({
    locals: { apiClient: { baseUrl: "http://api.test" } },
    request: { headers: { get: () => null } },
    fetch: () => Promise.resolve(answer()),
  }),
  query: (_schema: unknown, fn: unknown) => fn,
  command: (_schema: unknown, fn: unknown) => fn,
  form: (_schema: unknown, fn: unknown) => fn,
}));

vi.spyOn(console, "error").mockImplementation(() => {});

const { approveDeviceForm, denyDeviceForm } = await import("./oauth.remote");

type Handler = (
  data: { user_code: string },
  issue: { user_code: (message: string) => { message: string } }
) => Promise<unknown>;

const issue = { user_code: (message: string) => ({ message }) };

const FALLBACK = "The device code has expired or is no longer valid";

/** The sentence the device page shows beside the code field. */
async function shownFor(handler: unknown, response: Response): Promise<string> {
  answer = () => response;

  try {
    await (handler as Handler)({ user_code: "ABCD-EFGH" }, issue);
  } catch (thrown) {
    // SvelteKit types the guard as `ActionFailure`, but `invalid` throws a
    // `ValidationError` carrying the issues it was handed.
    if (isValidationError(thrown)) {
      const { issues } = thrown as unknown as { issues: { message: string }[] };
      return issues[0].message;
    }
    throw thrown;
  }

  throw new Error("the form resolved where it was expected to reject");
}

const oauthRefusal = (errorDescription?: string) =>
  new Response(
    JSON.stringify({
      error: "invalid_grant",
      ...(errorDescription && { error_description: errorDescription }),
    }),
    { status: 400 }
  );

describe.each([
  ["approving", approveDeviceForm],
  ["denying", denyDeviceForm],
])("%s a device code the API refuses", (_, handler) => {
  it("shows the API's own sentence", async () => {
    const shown = await shownFor(
      handler,
      oauthRefusal("Device code is invalid, expired, or already processed.")
    );

    expect(shown).toBe(
      "Device code is invalid, expired, or already processed."
    );
  });

  it("keeps its own sentence when the API gave none", async () => {
    expect(await shownFor(handler, oauthRefusal())).toBe(FALLBACK);
    expect(await shownFor(handler, new Response("", { status: 400 }))).toBe(
      FALLBACK
    );
  });
});
