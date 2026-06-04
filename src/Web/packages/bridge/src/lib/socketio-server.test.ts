import { describe, it, expect, vi } from 'vitest';
import { createServer } from 'http';
import SocketIOServer, { pickHandshakeHost, resolveTenantSlug } from './socketio-server.js';
import type { TenantAuthorizer } from './tenant-authorizer.js';

describe('resolveTenantSlug', () => {
  const base = 'nocturne.run';

  it('resolves a subdomain to its slug', () => {
    expect(resolveTenantSlug('rhys.nocturne.run', base, [])).toBe('rhys');
  });

  it('resolves the apex domain to the sole tenant', () => {
    expect(resolveTenantSlug('nocturne.run', base, ['only'])).toBe('only');
  });

  it('returns null on the apex when more than one tenant exists', () => {
    expect(resolveTenantSlug('nocturne.run', base, ['a', 'b'])).toBeNull();
  });

  it('returns null on the apex when no tenants are known yet', () => {
    expect(resolveTenantSlug('nocturne.run', base, [])).toBeNull();
  });

  it('returns null for a foreign domain', () => {
    expect(resolveTenantSlug('evil.com', base, ['only'])).toBeNull();
  });

  it('ignores a port on the host', () => {
    expect(resolveTenantSlug('rhys.nocturne.run:443', base, [])).toBe('rhys');
  });
});

describe('pickHandshakeHost', () => {
  it('prefers X-Forwarded-Host over Host', () => {
    expect(pickHandshakeHost({ 'x-forwarded-host': 'rhys.nocturne.run', host: 'web:5173' })).toBe(
      'rhys.nocturne.run',
    );
  });

  it('falls back to Host when no forwarded host is present', () => {
    expect(pickHandshakeHost({ host: 'rhys.nocturne.run' })).toBe('rhys.nocturne.run');
  });

  it('takes the first value when the forwarded host is repeated', () => {
    expect(pickHandshakeHost({ 'x-forwarded-host': ['a.nocturne.run', 'b.nocturne.run'] })).toBe(
      'a.nocturne.run',
    );
  });

  it('returns undefined when no host header is present', () => {
    expect(pickHandshakeHost({})).toBeUndefined();
  });
});

type HandshakeHeaders = Record<string, string | string[] | undefined>;

function makeServer(
  authorize: boolean | Error,
  tenantSlugs: string[] = [],
): { server: SocketIOServer; isAuthorized: ReturnType<typeof vi.fn> } {
  const isAuthorized =
    authorize instanceof Error
      ? vi.fn(async () => {
          throw authorize;
        })
      : vi.fn(async () => authorize);
  const authorizer = { isAuthorized } as unknown as TenantAuthorizer;
  const server = new SocketIOServer(createServer(), {}, 'nocturne.run', tenantSlugs, authorizer);
  return { server, isAuthorized };
}

function fakeSocket(headers: HandshakeHeaders) {
  return { id: 'sock1', handshake: { headers }, data: {} as Record<string, unknown> };
}

describe('SocketIOServer.authorizeHandshake', () => {
  it('rejects when the host resolves to no tenant', async () => {
    const { server, isAuthorized } = makeServer(true);
    const next = vi.fn();

    await server.authorizeHandshake(fakeSocket({ host: 'evil.com' }) as never, next);

    expect(next).toHaveBeenCalledTimes(1);
    expect(next.mock.calls[0][0]).toBeInstanceOf(Error);
    expect(next.mock.calls[0][0].message).toBe('tenant_unresolved');
    expect(isAuthorized).not.toHaveBeenCalled();
  });

  it('rejects an unauthorized connection to a private tenant', async () => {
    const { server, isAuthorized } = makeServer(false);
    const next = vi.fn();
    const socket = fakeSocket({ 'x-forwarded-host': 'rhys.nocturne.run', cookie: 'session=bad' });

    await server.authorizeHandshake(socket as never, next);

    expect(isAuthorized).toHaveBeenCalledWith('rhys.nocturne.run', 'session=bad');
    expect(next.mock.calls[0][0].message).toBe('unauthorized');
    expect(socket.data.tenantSlug).toBeUndefined();
  });

  it('admits an authorized connection and tags the socket with its tenant', async () => {
    const { server } = makeServer(true);
    const next = vi.fn();
    const socket = fakeSocket({ 'x-forwarded-host': 'rhys.nocturne.run', cookie: 'session=good' });

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBe('rhys');
  });

  it('admits a public-tenant connection that carries no cookie', async () => {
    const { server, isAuthorized } = makeServer(true);
    const next = vi.fn();
    const socket = fakeSocket({ 'x-forwarded-host': 'public.nocturne.run' });

    await server.authorizeHandshake(socket as never, next);

    expect(isAuthorized).toHaveBeenCalledWith('public.nocturne.run', undefined);
    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBe('public');
  });

  it('admits an apex connection for the sole tenant', async () => {
    const { server } = makeServer(true, ['only']);
    const next = vi.fn();
    const socket = fakeSocket({ host: 'nocturne.run' });

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBe('only');
  });

  it('rejects with no admission when authorization throws (fails closed)', async () => {
    const { server } = makeServer(new Error('boom'));
    const next = vi.fn();
    const socket = fakeSocket({ 'x-forwarded-host': 'rhys.nocturne.run', cookie: 'session=x' });

    await server.authorizeHandshake(socket as never, next);

    expect(next.mock.calls[0][0].message).toBe('authorization_error');
    expect(socket.data.tenantSlug).toBeUndefined();
  });
});
