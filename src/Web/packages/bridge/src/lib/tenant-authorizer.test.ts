import { describe, it, expect, vi } from 'vitest';
import { TenantAuthorizer, type FetchLike } from './tenant-authorizer.js';

function authorizerWith(fetchImpl: FetchLike): TenantAuthorizer {
  return new TenantAuthorizer({
    apiBaseUrl: 'http://api:8080/',
    fetch: fetchImpl,
    timeoutMs: 1000,
  });
}

describe('TenantAuthorizer.isAuthorized', () => {
  it('authorizes when the read probe returns 200 (authed member or public tenant)', async () => {
    const fetch = vi.fn(async () => ({ ok: true, status: 200 }));
    const auth = authorizerWith(fetch as unknown as FetchLike);

    await expect(auth.isAuthorized('rhys.nocturne.run', 'session=abc')).resolves.toBe(true);
  });

  it('rejects when the read probe returns 401 (private tenant, no/invalid credentials)', async () => {
    const fetch = vi.fn(async () => ({ ok: false, status: 401 }));
    const auth = authorizerWith(fetch as unknown as FetchLike);

    await expect(auth.isAuthorized('rhys.nocturne.run', undefined)).resolves.toBe(false);
  });

  it('rejects a cookie scoped to another tenant (API membership check fails -> 401)', async () => {
    const fetch = vi.fn(async () => ({ ok: false, status: 401 }));
    const auth = authorizerWith(fetch as unknown as FetchLike);

    await expect(
      auth.isAuthorized('rhys.nocturne.run', 'session=for-another-tenant'),
    ).resolves.toBe(false);
  });

  it('probes the read endpoint /api/v1/entries, forwarding the original Host and cookie', async () => {
    const fetch = vi.fn(async () => ({ ok: true, status: 200 }));
    const auth = authorizerWith(fetch as unknown as FetchLike);

    await auth.isAuthorized('rhys.nocturne.run', 'session=abc');

    expect(fetch).toHaveBeenCalledTimes(1);
    const [url, init] = fetch.mock.calls[0] as [string, { headers?: Record<string, string> }];
    expect(url).toBe('http://api:8080/api/v1/entries?count=1');
    expect(init.headers?.['X-Forwarded-Host']).toBe('rhys.nocturne.run');
    expect(init.headers?.['Cookie']).toBe('session=abc');
  });

  it('omits the Cookie header when the handshake carries no cookie (public path)', async () => {
    const fetch = vi.fn(async () => ({ ok: true, status: 200 }));
    const auth = authorizerWith(fetch as unknown as FetchLike);

    await auth.isAuthorized('public.nocturne.run', undefined);

    const [, init] = fetch.mock.calls[0] as [string, { headers?: Record<string, string> }];
    expect(init.headers && 'Cookie' in init.headers).toBe(false);
  });

  it('fails closed when the probe throws (e.g. the API is unreachable)', async () => {
    const fetch = vi.fn(async () => {
      throw new Error('network down');
    });
    const auth = authorizerWith(fetch as unknown as FetchLike);

    await expect(auth.isAuthorized('rhys.nocturne.run', 'session=abc')).resolves.toBe(false);
  });
});
