import { describe, it, expect, vi, beforeEach } from "vitest";
import { isHttpError, isRedirect } from "@sveltejs/kit";

let request: Request;
let answer: () => Response;
const sent: { url: string; init: RequestInit }[] = [];

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({
    locals: { apiClient: { baseUrl: "http://api.test" } },
    request,
    fetch: (url: string, init: RequestInit) => {
      sent.push({ url, init });
      return Promise.resolve(answer());
    },
  }),
  query: (_schema: unknown, fn: unknown) => fn,
  command: (_schema: unknown, fn: unknown) => fn,
  form: (_schema: unknown, fn: unknown) => fn,
}));

const { consentForm } = await import("./oauth.remote");

type Handler = (data: Record<string, string>, issue: Record<string, (m: string) => unknown>) => Promise<unknown>;

const approval = {
  client_id: "client",
  redirect_uri: "https://app.example/callback",
  scope: "glucose.read",
  code_challenge: "challenge",
  approved: "true",
};

async function submit(): Promise<unknown> {
  try {
    return await (consentForm as unknown as Handler)(approval, { client_id: (m) => m });
  } catch (thrown) {
    return thrown;
  }
}

function forwarded(): Headers {
  return new Headers(sent[0].init.headers as Record<string, string>);
}

describe("the consent proxy", () => {
  beforeEach(() => {
    sent.length = 0;
    answer = () => new Response(null, { status: 302, headers: { Location: "https://app.example/callback?code=x" } });
  });

  it("forwards the client-facing scheme and host the gateway passed on", async () => {
    request = new Request("http://web.internal/oauth/consent", {
      headers: { "x-forwarded-proto": "https", "x-forwarded-host": "dev.nocturne.example", host: "web.internal" },
    });

    expect(isRedirect(await submit())).toBe(true);
    expect(sent[0].url).toBe("http://api.test/api/oauth/authorize");
    expect(forwarded().get("X-Forwarded-Proto")).toBe("https");
    expect(forwarded().get("X-Forwarded-Host")).toBe("dev.nocturne.example");
  });

  it("forwards a scheme even for a request that carries a bare host and no scheme header", async () => {
    request = new Request("https://dev.nocturne.example/oauth/consent", { headers: { host: "dev.nocturne.example" } });

    await submit();

    expect(forwarded().get("X-Forwarded-Proto")).toBe("https");
    expect(forwarded().get("X-Forwarded-Host")).toBe("dev.nocturne.example");
  });

  it("answers a response it does not expect with a 502 that names the status", async () => {
    request = new Request("https://dev.nocturne.example/oauth/consent", { headers: { host: "dev.nocturne.example" } });
    answer = () => new Response("", { status: 500 });

    const thrown = await submit();

    expect(isHttpError(thrown) && thrown.status).toBe(502);
    expect(isHttpError(thrown) && thrown.body.message).toBe("The authorization server sent an unexpected response (status 500).");
  });
});
