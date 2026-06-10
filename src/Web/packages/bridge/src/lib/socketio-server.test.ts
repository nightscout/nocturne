import { describe, it, expect, vi } from 'vitest';
import { createServer } from 'http';
import SocketIOServer, { pickHandshakeHost, resolveTenantSlug } from './socketio-server.js';
import { signHandshakeTicket } from './handshake-ticket.js';

const SECRET = 'test-instance-key-0123456789';

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

function makeServer(tenantSlugs: string[] = []): SocketIOServer {
  return new SocketIOServer(createServer(), {}, 'nocturne.run', tenantSlugs, SECRET);
}

function fakeSocket(headers: HandshakeHeaders, auth: { token?: string } = {}) {
  return { id: 'sock1', handshake: { headers, auth }, data: {} as Record<string, unknown> };
}

describe('SocketIOServer.authorizeHandshake', () => {
  it('rejects when the host resolves to no tenant', async () => {
    const server = makeServer();
    const next = vi.fn();

    await server.authorizeHandshake(
      fakeSocket({ host: 'evil.com' }, { token: signHandshakeTicket(SECRET, 'evil.com') }) as never,
      next,
    );

    expect(next).toHaveBeenCalledTimes(1);
    expect(next.mock.calls[0][0]).toBeInstanceOf(Error);
    expect(next.mock.calls[0][0].message).toBe('tenant_unresolved');
  });

  it('rejects a connection that carries no ticket', async () => {
    const server = makeServer();
    const next = vi.fn();
    const socket = fakeSocket({ 'x-forwarded-host': 'rhys.nocturne.run' });

    await server.authorizeHandshake(socket as never, next);

    expect(next.mock.calls[0][0].message).toBe('unauthorized');
    expect(socket.data.tenantSlug).toBeUndefined();
  });

  it('rejects a tampered ticket', async () => {
    const server = makeServer();
    const next = vi.fn();
    const ticket = signHandshakeTicket(SECRET, 'rhys.nocturne.run');
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: ticket.slice(0, -1) + (ticket.endsWith('a') ? 'b' : 'a') },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next.mock.calls[0][0].message).toBe('unauthorized');
    expect(socket.data.tenantSlug).toBeUndefined();
  });

  it('rejects a ticket minted for a different host (no cross-tenant replay)', async () => {
    const server = makeServer();
    const next = vi.fn();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'someone-else.nocturne.run') },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next.mock.calls[0][0].message).toBe('unauthorized');
    expect(socket.data.tenantSlug).toBeUndefined();
  });

  it('rejects an expired ticket', async () => {
    const server = makeServer();
    const next = vi.fn();
    // Sign a ticket that expired one minute ago.
    const expired = signHandshakeTicket(SECRET, 'rhys.nocturne.run', -60_000);
    const socket = fakeSocket({ 'x-forwarded-host': 'rhys.nocturne.run' }, { token: expired });

    await server.authorizeHandshake(socket as never, next);

    expect(next.mock.calls[0][0].message).toBe('unauthorized');
    expect(socket.data.tenantSlug).toBeUndefined();
  });

  it('admits a valid ticket and tags the socket with its tenant', async () => {
    const server = makeServer();
    const next = vi.fn();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run') },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBe('rhys');
  });

  it('ignores a port difference between the ticket and the connection host', async () => {
    const server = makeServer();
    const next = vi.fn();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run:443') },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBe('rhys');
  });

  it('admits an apex connection for the sole tenant with an apex ticket', async () => {
    const server = makeServer(['only']);
    const next = vi.fn();
    const socket = fakeSocket(
      { host: 'nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'nocturne.run') },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBe('only');
  });
});
