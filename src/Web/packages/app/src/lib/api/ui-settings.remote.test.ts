import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { ApiException } from "$api-clients";
import { errorMessage } from "$lib/forms/submit-error";

let upstream: () => Promise<unknown>;
let headers: Map<string, string>;

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({
    locals: { apiClient: { uiSettings: { getUISettings: () => upstream() } } },
    request: { headers: { get: (name: string) => headers.get(name) ?? null } },
    url: new URL("https://app.example.test/settings/appearance?tab=theme"),
  }),
  query: (fn: unknown) => fn,
  command: (_schema: unknown, fn: unknown) => fn,
}));

const { getUiSettings } = await import("./ui-settings.remote");

/**
 * A reading surface forwards a server-written body verbatim, so this has to be
 * a whole sentence with a remedy rather than a placeholder.
 */
const FIXED = "We couldn't load your settings. Refresh the page to try again.";

const apiException = (message: string, status: number, body: string) =>
  new ApiException(message, status, body, {}, null);

/** What this query threw, which is never the thing it caught. */
async function rejectionFor(failure: unknown): Promise<unknown> {
  upstream = () => Promise.reject(failure);

  try {
    await getUiSettings();
  } catch (rejection) {
    return rejection;
  }

  throw new Error("getUiSettings resolved where it was expected to reject");
}

/** The reason it put in `error(status, message)`, read as the store reads it. */
const reasonFor = async (failure: unknown) =>
  errorMessage(await rejectionFor(failure));

/**
 * `SettingsStore` renders this query's rejection through `remoteErrorMessage`,
 * whose half of the policy is documented at `RemoteErrorPolicy`. Nothing that
 * stands in for this query can observe what its catch answers with, so the
 * answer is pinned here.
 */
describe("a failed UI settings read", () => {
  beforeEach(() => {
    headers = new Map();
    vi.spyOn(console, "error").mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("answers a ProblemDetails body with its own sentence", async () => {
    const reason = await reasonFor(
      apiException(
        "A server side error occurred.",
        500,
        '{"title":"Internal Server Error","detail":"Internal server error","status":500}'
      )
    );

    expect(reason).toBe(FIXED);
  });

  it("answers an error page body with its own sentence", async () => {
    const reason = await reasonFor(
      apiException(
        "An unexpected server error occurred.",
        502,
        "<html><head><title>502 Bad Gateway</title></head><body><center><hr>nginx/1.27.0</center></body></html>"
      )
    );

    expect(reason).toBe(FIXED);
  });

  it("keeps a thrown ProblemDetails object's own wording out of the reason", async () => {
    const reason = await reasonFor({
      status: 400,
      title: "Bad Request",
      detail: "The tenant schema is missing column features_json",
    });

    expect(reason).toBe(FIXED);
  });

  it("carries nothing from upstream in the reason it reports", async () => {
    const reasons = [
      await reasonFor(
        apiException(
          "A server side error occurred.",
          500,
          '{"detail":"npgsql: connection reset"}'
        )
      ),
      await reasonFor(new Error("getaddrinfo ENOTFOUND nocturne.internal")),
    ];

    for (const reason of reasons) {
      expect(reason).toBe(FIXED);
      expect(reason).not.toMatch(/npgsql|ENOTFOUND|nginx|server (side )?error/i);
    }
  });

  /**
   * The settings pages sit behind the authenticated layout, so reporting an
   * expired session as a failed read would replace the form with a card
   * offering no way back to signing in.
   */
  it("sends an expired session to the login route", async () => {
    const rejection = await rejectionFor(
      apiException("Not authenticated.", 401, "")
    );

    expect(rejection).toMatchObject({
      status: 302,
      location:
        "/auth/login?returnUrl=%2Fsettings%2Fappearance%3Ftab%3Dtheme",
    });
  });

  it("refuses a share host rather than redirecting it", async () => {
    headers = new Map([["x-forwarded-host", "abc123.share.example.test"]]);

    const rejection = await rejectionFor(
      apiException("Not authenticated.", 401, "")
    );

    expect(rejection).toMatchObject({ status: 401 });
    expect(rejection).not.toHaveProperty("location");
  });
});
