/** One credential of an outbound Scalar request, as its `onBeforeRequest` hook receives it. */
export type ScalarSecurityEntry = { format?: string; value?: string };

type Fetch = (input: string, init?: RequestInit) => Promise<Response>;

/** Renew this long before expiry, so a request never leaves with a token about to lapse. */
const RENEW_MARGIN_MS = 60_000;

/**
 * Access tokens for the demo tenant's shared member, from its anonymous session endpoint.
 * The token lasts minutes and the page may stay open for hours, so each request swaps in a
 * current one, but only where the request carries a token this source issued: a token the
 * reader pasted is theirs and goes out unchanged.
 */
export function createDemoTokenSource(demoUrl: string, fetchImpl: Fetch = fetch, now = () => Date.now()) {
    const issued = new Set<string>();
    let current: { token: string; expiresAt: number } | null = null;
    let pending: Promise<string> | null = null;

    async function issue(): Promise<string> {
        // No cookies: the session cookie the endpoint also sets is for the demo's own pages.
        const response = await fetchImpl(`${demoUrl}/api/v4/demo/session?format=json`, {
            credentials: "omit",
        });
        if (!response.ok) throw new Error(`Demo session request failed: ${response.status}`);

        const body: unknown = await response.json();
        if (
            typeof body !== "object" ||
            body === null ||
            !("accessToken" in body) ||
            typeof body.accessToken !== "string" ||
            !("expiresInSeconds" in body) ||
            typeof body.expiresInSeconds !== "number"
        ) {
            throw new Error("Demo session response has no access token");
        }

        issued.add(body.accessToken);
        current = { token: body.accessToken, expiresAt: now() + body.expiresInSeconds * 1000 };
        return body.accessToken;
    }

    async function token(): Promise<string> {
        if (current && now() < current.expiresAt - RENEW_MARGIN_MS) return current.token;
        pending ??= issue().finally(() => {
            pending = null;
        });
        return pending;
    }

    async function refresh(security: ScalarSecurityEntry[]): Promise<void> {
        const stale = security.filter(
            (entry) => entry.format === "bearer" && entry.value !== undefined && issued.has(entry.value),
        );
        if (stale.length === 0) return;

        const fresh = await token();
        for (const entry of stale) entry.value = fresh;
    }

    return { token, refresh };
}
