/** One credential of an outbound Scalar request, as its `onBeforeRequest` hook receives it. */
export type ScalarSecurityEntry = { format?: string; value?: string };

type Fetch = (input: string, init?: RequestInit) => Promise<Response>;

/**
 * The bearer value the reference starts with. A demo session is opened only when a request is
 * sent, so reading the reference opens none.
 */
export const DEMO_TOKEN_PLACEHOLDER = "demo-token";

/** Whether a rejection is a fetch aborted through its signal. */
export function isAbort(error: unknown): boolean {
    return error instanceof DOMException && error.name === "AbortError";
}

/** Renew this long before expiry, so a request never leaves with a token about to lapse. */
const RENEW_MARGIN_MS = 60_000;

/** A token checked this recently is reused without asking the demo again. */
const VERIFIED_FOR_MS = 60_000;

/**
 * Access tokens for the demo tenant's shared member, from its anonymous session endpoint.
 *
 * Every session is a row the demo caps and evicts oldest-first, and the endpoint shares a rate
 * limit with the demo's own sign-in, so one source serves the whole page load and opens a session
 * only for a request that needs one. A demo reset revokes every token before it expires, so a
 * cached token is checked against the demo before it is reused.
 *
 * Only a request carrying the placeholder or a token this source issued is changed: a token the
 * reader pasted is theirs and goes out unchanged.
 */
export function createDemoTokenSource(demoUrl: string, fetchImpl: Fetch = fetch, now = () => Date.now()) {
    const sessionUrl = `${demoUrl}/api/auth/oidc/session`;
    const issued = new Set<string>();
    let current: { token: string; expiresAt: number; verifiedAt: number } | null = null;
    let pending: Promise<string> | null = null;

    async function issue(): Promise<string> {
        // No cookies: the session cookie the endpoint also sets is for the demo's own pages.
        const response = await fetchImpl(`${demoUrl}/api/v4/demo/session?format=json`, {
            credentials: "omit",
        });
        if (!response.ok) throw new Error(`The demo refused a session: ${response.status}`);

        const body: unknown = await response.json();
        if (
            typeof body !== "object" ||
            body === null ||
            !("accessToken" in body) ||
            typeof body.accessToken !== "string" ||
            !("expiresInSeconds" in body) ||
            typeof body.expiresInSeconds !== "number"
        ) {
            throw new Error("The demo session response has no access token");
        }

        issued.add(body.accessToken);
        const at = now();
        current = { token: body.accessToken, expiresAt: at + body.expiresInSeconds * 1000, verifiedAt: at };
        return body.accessToken;
    }

    async function isLive(token: string): Promise<boolean> {
        const response = await fetchImpl(sessionUrl, {
            credentials: "omit",
            headers: { Authorization: `Bearer ${token}` },
        });
        if (!response.ok) return false;
        const body: unknown = await response.json();
        return typeof body === "object" && body !== null && "isAuthenticated" in body && body.isAuthenticated === true;
    }

    async function resolve(): Promise<string> {
        const held = current;
        if (held && now() < held.expiresAt - RENEW_MARGIN_MS) {
            if (now() - held.verifiedAt < VERIFIED_FOR_MS) return held.token;
            if (await isLive(held.token)) {
                held.verifiedAt = now();
                return held.token;
            }
        }
        return issue();
    }

    function token(): Promise<string> {
        pending ??= resolve().finally(() => {
            pending = null;
        });
        return pending;
    }

    /**
     * Whether the demo answers this page's origin. Anonymous and opens no session, so it decides
     * whether the reference can send requests at all.
     */
    async function reachable(signal?: AbortSignal): Promise<boolean> {
        try {
            const response = await fetchImpl(sessionUrl, { credentials: "omit", signal });
            return response.ok;
        } catch (error) {
            // A refused origin and an unreachable host both surface as a TypeError.
            if (error instanceof TypeError) return false;
            throw error;
        }
    }

    async function refresh(security: ScalarSecurityEntry[]): Promise<void> {
        const ours = security.filter(
            (entry) =>
                entry.format === "bearer" &&
                (entry.value === DEMO_TOKEN_PLACEHOLDER || (entry.value !== undefined && issued.has(entry.value))),
        );
        if (ours.length === 0) return;

        const fresh = await token();
        for (const entry of ours) entry.value = fresh;
    }

    return { token, reachable, refresh };
}
