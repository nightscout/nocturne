import { describe, expect, it, vi } from "vitest";
import { createDemoTokenSource, DEMO_TOKEN_PLACEHOLDER, isAbort } from "./demo-token.js";

const DEMO = "https://demo.example";

/** The demo's two endpoints. `live` decides whether a token still authenticates. */
function demo(live: (token: string) => boolean = () => true) {
    let n = 0;
    return vi.fn(async (url: string, init?: RequestInit) => {
        if (url.endsWith("/api/v4/demo/session?format=json")) {
            n += 1;
            return Response.json({ accessToken: `token-${n}`, expiresInSeconds: 900 });
        }
        const auth = new Headers(init?.headers).get("Authorization");
        return Response.json({ isAuthenticated: auth !== null && live(auth.slice("Bearer ".length)) });
    });
}

const sessionsOpened = (fetchImpl: ReturnType<typeof demo>) =>
    fetchImpl.mock.calls.filter(([url]) => url.includes("/api/v4/demo/session")).length;

describe("createDemoTokenSource", () => {
    it("opens no session until a request needs one", async () => {
        const fetchImpl = demo();
        const source = createDemoTokenSource(DEMO, fetchImpl);

        expect(await source.reachable()).toBe(true);
        expect(sessionsOpened(fetchImpl)).toBe(0);
    });

    it("asks the demo for a session without cookies", async () => {
        const fetchImpl = demo();
        const source = createDemoTokenSource(DEMO, fetchImpl);

        expect(await source.token()).toBe("token-1");
        expect(fetchImpl).toHaveBeenCalledWith(`${DEMO}/api/v4/demo/session?format=json`, { credentials: "omit" });
    });

    it("replaces the placeholder on a request", async () => {
        const source = createDemoTokenSource(DEMO, demo());
        const security = [{ format: "bearer", value: DEMO_TOKEN_PLACEHOLDER }];

        await source.refresh(security);

        expect(security[0].value).toBe("token-1");
    });

    it("reuses a checked token without opening another session", async () => {
        let clock = 0;
        const fetchImpl = demo();
        const source = createDemoTokenSource(DEMO, fetchImpl, () => clock);

        await source.token();
        clock = 30_000;
        expect(await source.token()).toBe("token-1");
        clock = 300_000;
        expect(await source.token()).toBe("token-1");
        expect(sessionsOpened(fetchImpl)).toBe(1);
    });

    it("opens a new session when a demo reset revoked the token", async () => {
        let clock = 0;
        const revoked = new Set<string>();
        const source = createDemoTokenSource(DEMO, demo((t) => !revoked.has(t)), () => clock);
        const security = [{ format: "bearer", value: await source.token() }];

        revoked.add("token-1");
        clock = 120_000;
        await source.refresh(security);

        expect(security[0].value).toBe("token-2");
    });

    it("opens a new session a minute before the token expires", async () => {
        let clock = 0;
        const source = createDemoTokenSource(DEMO, demo(), () => clock);

        await source.token();
        clock = 840_000;

        expect(await source.token()).toBe("token-2");
    });

    it("opens one session for concurrent requests", async () => {
        const fetchImpl = demo();
        const source = createDemoTokenSource(DEMO, fetchImpl);

        expect(await Promise.all([source.token(), source.token()])).toEqual(["token-1", "token-1"]);
        expect(sessionsOpened(fetchImpl)).toBe(1);
    });

    it("leaves a token the reader pasted unchanged", async () => {
        const fetchImpl = demo();
        const source = createDemoTokenSource(DEMO, fetchImpl);
        const security = [
            { format: "bearer", value: "their-own" },
            { format: undefined, value: "api-secret" },
        ];

        await source.refresh(security);

        expect(security.map((s) => s.value)).toEqual(["their-own", "api-secret"]);
        expect(fetchImpl).not.toHaveBeenCalled();
    });

    it("fails a request when the demo refuses a session", async () => {
        const source = createDemoTokenSource(DEMO, vi.fn(async () => new Response("", { status: 429 })));

        await expect(source.refresh([{ format: "bearer", value: DEMO_TOKEN_PLACEHOLDER }])).rejects.toThrow("429");
    });

    it("reports an origin the demo refuses as unreachable", async () => {
        const source = createDemoTokenSource(
            DEMO,
            vi.fn(async () => {
                throw new TypeError("Failed to fetch");
            }),
        );

        expect(await source.reachable()).toBe(false);
    });

    it("passes an abort through", async () => {
        const source = createDemoTokenSource(
            DEMO,
            vi.fn(async () => {
                throw new DOMException("aborted", "AbortError");
            }),
        );

        const error = await source.reachable().catch((e: unknown) => e);
        expect(isAbort(error)).toBe(true);
    });
});
