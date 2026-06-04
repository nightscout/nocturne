import logger from './logger.js';

/** Minimal fetch surface used by the authorizer, for injection in tests. */
export type FetchLike = (
  url: string,
  init?: { method?: string; headers?: Record<string, string>; signal?: AbortSignal },
) => Promise<{ ok: boolean; status: number }>;

export interface TenantAuthorizerConfig {
  /** Base URL of the Nocturne API (e.g. "http://api:8080"), no trailing slash required. */
  apiBaseUrl: string;
  /** Injected fetch implementation. Defaults to the global fetch. */
  fetch?: FetchLike;
  /** Per-request timeout in milliseconds. */
  timeoutMs?: number;
}

/**
 * Authorizes a Socket.IO handshake for a tenant's live data stream by replaying
 * the browser's read against the API. It forwards the handshake's original Host
 * (the same header the REST read path receives) and session cookie to a read of
 * `GET /api/v1/entries?count=1`. That endpoint carries the same
 * `[Authorize(Policy = HasPermissions)]` gate as every data read, so probing it
 * mirrors the read policy exactly and cannot drift from it — including for
 * public-read tenants, which the API admits unauthenticated. The Loop probe
 * `/api/v1/experiments/test` is NOT equivalent: its `[RequireRead]` filter
 * rejects every unauthenticated request before checking permissions, so it
 * would deny public dashboards. The API resolves the tenant from the Host —
 * subdomain or apex single-tenant — and applies its standard read policy:
 *   - authenticated member of the tenant -> 200
 *   - public-read tenant -> 200 even without a cookie
 *   - private tenant without valid credentials -> 401
 *   - cookie scoped to a different tenant -> fails the API's membership check
 *     and falls through to the resolved tenant's public access, so it is
 *     rejected unless that tenant is public-read
 * A data-less tenant still returns 200 with an empty array, so an authorized
 * connection is never rejected for lack of entries.
 */
export class TenantAuthorizer {
  private readonly probeUrl: string;
  private readonly fetch: FetchLike;
  private readonly timeoutMs: number;

  constructor(config: TenantAuthorizerConfig) {
    this.probeUrl = `${config.apiBaseUrl.replace(/\/+$/, '')}/api/v1/entries?count=1`;
    this.fetch = config.fetch ?? (globalThis.fetch as unknown as FetchLike);
    this.timeoutMs = config.timeoutMs ?? 5000;
  }

  /**
   * Returns true if a connection carrying the given Host and cookie header is
   * allowed to read the data of the tenant that Host resolves to.
   */
  async isAuthorized(host: string, cookieHeader: string | undefined): Promise<boolean> {
    const headers: Record<string, string> = {
      'X-Forwarded-Host': host,
      // Bypass the API's server-side response cache. UseResponseCaching runs before
      // authentication and keys on the raw Host, which is the same constant internal
      // host for every tenant's probe — so without this, an authorized probe's cached
      // 200 would be replayed to authorize a later unauthenticated handshake. 'no-cache'
      // forces the cache to revalidate so the request reaches the auth pipeline;
      // 'no-store' keeps this probe out of the cache. ('no-store' alone does NOT bypass
      // the lookup.)
      'Cache-Control': 'no-cache, no-store',
    };
    if (cookieHeader) headers['Cookie'] = cookieHeader;

    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.timeoutMs);
    try {
      const response = await this.fetch(this.probeUrl, {
        method: 'GET',
        headers,
        signal: controller.signal,
      });
      if (!response.ok) {
        logger.warn(
          `Socket.IO authorization denied for host ${host}: API returned ${response.status}`,
        );
      }
      return response.ok;
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      logger.warn(`Socket.IO authorization probe failed for host ${host}: ${message}`);
      return false;
    } finally {
      clearTimeout(timer);
    }
  }
}
