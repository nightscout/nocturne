import { describe, expect, it, vi } from "vitest";
import { createDemoTokenSource } from "./demo-token.js";

function sessionFetch() {
    let n = 0;
    return vi.fn(async () => {
        n += 1;
        return new Response(JSON.stringify({ accessToken: `token-${n}`, expiresInSeconds: 900 }), {
            status: 200,
        });
    });
}

describe("createDemoTokenSource", () => {
    it("asks the demo for a session without cookies", async () => {
        const fetchImpl = sessionFetch();
        const source = createDemoTokenSource("https://demo.example", fetchImpl);

        expect(await source.token()).toBe("token-1");
        expect(fetchImpl).toHaveBeenCalledWith("https://demo.example/api/v4/demo/session?format=json", {
            credentials: "omit",
        });
    });

    it("reuses a token until a minute before it expires", async () => {
        let clock = 0;
        const fetchImpl = sessionFetch();
        const source = createDemoTokenSource("https://demo.example", fetchImpl, () => clock);

        await source.token();
        clock = 839_000;
        expect(await source.token()).toBe("token-1");
        clock = 840_000;
        expect(await source.token()).toBe("token-2");
        expect(fetchImpl).toHaveBeenCalledTimes(2);
    });

    it("issues one session for concurrent callers", async () => {
        const fetchImpl = sessionFetch();
        const source = createDemoTokenSource("https://demo.example", fetchImpl);

        const tokens = await Promise.all([source.token(), source.token()]);

        expect(tokens).toEqual(["token-1", "token-1"]);
        expect(fetchImpl).toHaveBeenCalledTimes(1);
    });

    it("replaces an expired demo token on a request", async () => {
        let clock = 0;
        const source = createDemoTokenSource("https://demo.example", sessionFetch(), () => clock);
        const security = [{ format: "bearer", value: await source.token() }];

        clock = 900_000;
        await source.refresh(security);

        expect(security[0].value).toBe("token-2");
    });

    it("leaves a token the reader pasted unchanged", async () => {
        const fetchImpl = sessionFetch();
        const source = createDemoTokenSource("https://demo.example", fetchImpl);
        await source.token();
        const security = [
            { format: "bearer", value: "their-own" },
            { format: undefined, value: "api-secret" },
        ];

        await source.refresh(security);

        expect(security.map((s) => s.value)).toEqual(["their-own", "api-secret"]);
        expect(fetchImpl).toHaveBeenCalledTimes(1);
    });

    it("fails when the demo refuses a session", async () => {
        const source = createDemoTokenSource(
            "https://demo.example",
            vi.fn(async () => new Response("", { status: 404 })),
        );

        await expect(source.token()).rejects.toThrow("404");
    });
});
