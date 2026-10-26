import { createHmac } from 'node:crypto';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { createServer } from 'http';
import type { AddressInfo } from 'node:net';
import { io as connectClient, type Socket as ClientSocket } from 'socket.io-client';
import SocketIOServer, {
  isShareHost,
  pickHandshakeHost,
  resolveTenantSlug,
  SHARE_REALTIME_UNSUPPORTED,
} from './socketio-server.js';
import { signHandshakeTicket, type RealtimeGrant } from './handshake-ticket.js';
import MessageTranslator from './message-translator.js';

const SECRET = 'test-instance-key-0123456789';
const TENANT_ID = '0198a3c4-7e21-7b10-9d2f-5c6e7f809a1b';

/** The server, with `TENANT_ID` as the tenant every slug names. */
function servingTenant(server: SocketIOServer): SocketIOServer {
  server.isCurrentTenant = async (_slug, id) => id === TENANT_ID;
  server.holdsTenantNow = (_slug, id) => id === TENANT_ID;
  return server;
}

function grant(tenantRelay: boolean, extra: Partial<RealtimeGrant> = {}): RealtimeGrant {
  return { tenantRelay, categories: [], historyClamped: false, mayAck: false, ...extra };
}

describe('resolveTenantSlug', () => {
  const base = 'nocturne.run';

  it('resolves a subdomain to its slug', () => {
    expect(resolveTenantSlug('rhys.nocturne.run', base, null)).toBe('rhys');
  });

  it('resolves the apex domain to the tenant the API serves there', () => {
    expect(resolveTenantSlug('nocturne.run', base, 'only')).toBe('only');
  });

  it('returns null on the apex when the API serves no tenant there', () => {
    expect(resolveTenantSlug('nocturne.run', base, null)).toBeNull();
  });

  it('returns null for a foreign domain', () => {
    expect(resolveTenantSlug('evil.com', base, 'only')).toBeNull();
  });

  it('ignores a port on the host', () => {
    expect(resolveTenantSlug('rhys.nocturne.run:443', base, null)).toBe('rhys');
  });
});

describe('isShareHost', () => {
  const base = 'nocturne.run';

  it('names a {token}.share.{baseDomain} host a share host', () => {
    expect(isShareHost('abc123.share.nocturne.run', base)).toBe(true);
    expect(isShareHost('ABC123.SHARE.nocturne.run:443', base)).toBe(true);
  });

  it.each(['share.nocturne.run', 'rhys.nocturne.run', 'nocturne.run', 'abc.share.evil.com', 'a.b.share.nocturne.run'])(
    'does not name %s a share host',
    (host) => {
      expect(isShareHost(host, base)).toBe(false);
    },
  );

  it('does not name a missing host a share host', () => {
    expect(isShareHost(undefined, base)).toBe(false);
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

function makeServer(apexTenantSlug: string | null = null): SocketIOServer {
  return servingTenant(new SocketIOServer(createServer(), {}, 'nocturne.run', apexTenantSlug, SECRET));
}

function fakeSocket(headers: HandshakeHeaders, auth: { token?: string } = {}, query: { token?: string } = {}) {
  return {
    id: 'sock1',
    handshake: { headers, auth, query },
    data: {} as Record<string, unknown>,
    join: vi.fn(),
    on: vi.fn(),
    emit: vi.fn(),
  };
}

/** Admits `socket` through the handshake, then hands it to the server's own connection handler. */
async function connect(server: SocketIOServer, socket: ReturnType<typeof fakeSocket>) {
  await server.start();
  const next = vi.fn();
  await server.authorizeHandshake(socket as never, next);
  const handlers = server.getIO()!.of('/').listeners('connection');
  expect(handlers).toHaveLength(1);
  handlers[0](socket);
  await server.stop();
  return next;
}

/** A ticket signed with the instance key but carrying no admission, as minted before it existed. */
function ticketWithoutAdmission(host: string): string {
  const payload = Buffer.from(JSON.stringify({ h: host, t: TENANT_ID, exp: Date.now() + 60_000 }), 'utf-8')
    .toString('base64url');
  return `${payload}.${createHmac('sha256', SECRET).update(payload).digest('hex')}`;
}

describe('SocketIOServer.authorizeHandshake', () => {
  it('rejects when the host resolves to no tenant', async () => {
    const server = makeServer();
    const next = vi.fn();

    await server.authorizeHandshake(
      fakeSocket({ host: 'evil.com' }, { token: signHandshakeTicket(SECRET, 'evil.com', TENANT_ID, grant(true)) }) as never,
      next,
    );

    expect(next).toHaveBeenCalledTimes(1);
    expect(next.mock.calls[0][0]).toBeInstanceOf(Error);
    expect(next.mock.calls[0][0].message).toBe('tenant_unresolved');
  });

  it('admits a connection with no ticket but leaves it unauthorized', async () => {
    // Legacy clients send credentials only after connecting, so the socket is
    // allowed in — but it must not be authorized, which is what joins it to the
    // tenant room. Until `authorize` succeeds it receives nothing.
    const server = makeServer();
    const next = vi.fn();
    const socket = fakeSocket({ 'x-forwarded-host': 'rhys.nocturne.run' });

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBeUndefined();
    expect(socket.data.pendingTenantSlug).toBe('rhys');
  });

  it('rejects a tampered ticket', async () => {
    // A ticket that was offered but doesn't verify is still rejected outright —
    // only a connection offering no ticket at all falls through to the legacy path.
    const server = makeServer();
    const next = vi.fn();
    const ticket = signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true));
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: ticket.slice(0, -1) + (ticket.endsWith('a') ? 'b' : 'a') },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next.mock.calls[0][0].message).toBe('unauthorized');
    expect(socket.data.tenantSlug).toBeUndefined();
    expect(socket.data.pendingTenantSlug).toBeUndefined();
  });

  it('rejects a ticket minted for a different host (no cross-tenant replay)', async () => {
    const server = makeServer();
    const next = vi.fn();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'someone-else.nocturne.run', TENANT_ID, grant(true)) },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next.mock.calls[0][0].message).toBe('unauthorized');
    expect(socket.data.tenantSlug).toBeUndefined();
  });

  it('rejects an expired ticket so the client refetches instead of going quiet', async () => {
    const server = makeServer();
    const next = vi.fn();
    // Sign a ticket that expired one minute ago.
    const expired = signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true), -60_000);
    const socket = fakeSocket({ 'x-forwarded-host': 'rhys.nocturne.run' }, { token: expired });

    await server.authorizeHandshake(socket as never, next);

    expect(next.mock.calls[0][0].message).toBe('unauthorized');
    expect(socket.data.tenantSlug).toBeUndefined();
    expect(socket.data.pendingTenantSlug).toBeUndefined();
  });

  it('admits a valid ticket and tags the socket with its tenant', async () => {
    const server = makeServer();
    const next = vi.fn();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true)) },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBe('rhys');
  });

  it('rejects a ticket for a tenant its slug no longer names, asking about the slug the host resolved to', async () => {
    const server = makeServer();
    const isCurrentTenant = vi.fn(async (_slug: string, id: string) => id === TENANT_ID);
    server.isCurrentTenant = isCurrentTenant;
    const next = vi.fn();
    const replaced = '0198a3c4-0000-7000-8000-000000000000';
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run', replaced, grant(true)) },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(isCurrentTenant).toHaveBeenCalledWith('rhys', replaced);
    expect(next).toHaveBeenCalledWith(expect.objectContaining({ message: 'unauthorized' }));
    expect(socket.data.tenantSlug).toBeUndefined();
  });

  it('refuses a ticket whose tenant list changed between the check and the grant', async () => {
    const server = makeServer();
    const recreated = '0198a3c4-0000-7000-8000-000000000001';
    let current = TENANT_ID;
    server.holdsTenantNow = (_slug, id) => id === current;
    server.isCurrentTenant = async (_slug, id) => {
      const held = id === current;
      current = recreated;
      return held;
    };
    const next = vi.fn();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true)) },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledTimes(1);
    expect(next).toHaveBeenCalledWith(expect.objectContaining({ message: 'unauthorized' }));
    expect(socket.data.tenantSlug).toBeUndefined();
    expect(socket.data.tenantRelay).toBeUndefined();
  });

  it('rejects every ticket until it is told which tenant a slug names', async () => {
    const server = new SocketIOServer(createServer(), {}, 'nocturne.run', null, SECRET);
    const next = vi.fn();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true)) },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledWith(expect.objectContaining({ message: 'unauthorized' }));
  });

  it('ignores a port difference between the ticket and the connection host', async () => {
    const server = makeServer();
    const next = vi.fn();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run:443', TENANT_ID, grant(true)) },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBe('rhys');
  });

  it('admits an apex connection for the sole tenant with an apex ticket', async () => {
    const server = makeServer('only');
    const next = vi.fn();
    const socket = fakeSocket(
      { host: 'nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'nocturne.run', TENANT_ID, grant(true)) },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBe('only');
  });

  it('joins a member ticket to the tenant room', async () => {
    const server = makeServer();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true)) },
    );

    await connect(server, socket);

    expect(socket.join).toHaveBeenCalledWith('tenant:rhys');
  });

  it('joins a member ticket to its own subject room only', async () => {
    const server = makeServer();
    const subjectA = '0a5f2c1e-1111-4222-8333-444455556666';
    const subjectB = '0b6f2c1e-1111-4222-8333-444455556666';
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true, { subjectId: subjectA })) },
    );

    await connect(server, socket);

    expect(socket.join).toHaveBeenCalledWith('tenant:rhys');
    expect(socket.join).toHaveBeenCalledWith(`tenant:rhys:subject:${subjectA}`);
    expect(socket.join).not.toHaveBeenCalledWith(`tenant:rhys:subject:${subjectB}`);
    expect(socket.data.subjectId).toBe(subjectA);
  });

  it('joins a member ticket to the category rooms it was granted', async () => {
    const server = makeServer();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true, { categories: ['entries', 'care'] })) },
    );

    await connect(server, socket);

    const categoryRooms = socket.join.mock.calls
      .map(([room]) => String(room))
      .filter((room) => room.includes(':category:'));
    expect(categoryRooms).toEqual(['tenant:rhys:category:entries', 'tenant:rhys:category:care']);
  });

  it('joins a history-clamped member ticket to the tenant room and no category room', async () => {
    const server = makeServer();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      {
        token: signHandshakeTicket(
          SECRET,
          'rhys.nocturne.run',
          TENANT_ID,
          grant(true, { categories: ['entries', 'care'], historyClamped: true }),
        ),
      },
    );

    await connect(server, socket);

    expect(socket.join).toHaveBeenCalledWith('tenant:rhys');
    expect(socket.join.mock.calls.some(([room]) => String(room).includes(':category:'))).toBe(false);
  });

  it('joins no subject room for a ticket without a subject', async () => {
    const server = makeServer();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true)) },
    );

    await connect(server, socket);

    expect(socket.data.subjectId).toBeUndefined();
    expect(
      socket.join.mock.calls.some(([room]) => String(room).includes(':subject:')),
    ).toBe(false);
  });

  it.each([
    ['a restricted ticket', () => signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(false))],
    ['a ticket carrying no admission', () => ticketWithoutAdmission('rhys.nocturne.run')],
    [
      'a restricted ticket granted categories',
      () => signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(false, { categories: ['entries', 'care'] })),
    ],
    [
      'a restricted ticket naming a subject',
      () =>
        signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(false, { subjectId: '0a5f2c1e-1111-4222-8333-444455556666' })),
    ],
  ])('admits %s but keeps it out of every tenant room', async (_case, mint) => {
    // The tenant room carries every category and every member's in-app
    // notifications; a guest link or anonymous share holds single categories.
    const server = makeServer();
    const socket = fakeSocket({ 'x-forwarded-host': 'rhys.nocturne.run' }, { token: mint() });

    const next = await connect(server, socket);

    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBe('rhys');
    expect(socket.join).not.toHaveBeenCalled();
  });

  it('admits a legacy access token in the query unauthorized, keeping it for authorize', async () => {
    const server = makeServer();
    const next = vi.fn();
    const socket = fakeSocket({ 'x-forwarded-host': 'rhys.nocturne.run' }, {}, { token: 'loopfollow-a1b2c3d4e5f60718' });

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBeUndefined();
    expect(socket.data.pendingTenantSlug).toBe('rhys');
    expect(socket.data.handshakeToken).toBe('loopfollow-a1b2c3d4e5f60718');
  });

  it('still rejects a tampered ticket in the query', async () => {
    const server = makeServer();
    const next = vi.fn();
    const ticket = signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true));
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      {},
      { token: ticket.slice(0, -1) + (ticket.endsWith('a') ? 'b' : 'a') },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next.mock.calls[0][0].message).toBe('unauthorized');
    expect(socket.data.pendingTenantSlug).toBeUndefined();
    expect(socket.data.handshakeToken).toBeUndefined();
  });

  it('admits a valid ticket in the query as a ticket', async () => {
    const server = makeServer();
    const next = vi.fn();
    const socket = fakeSocket(
      { 'x-forwarded-host': 'rhys.nocturne.run' },
      {},
      { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true)) },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next).toHaveBeenCalledWith();
    expect(socket.data.tenantSlug).toBe('rhys');
    expect(socket.data.handshakeToken).toBeUndefined();
  });
});

describe('SocketIOServer legacy LoopFollow connection', () => {
  const clients: ClientSocket[] = [];
  const TOKEN = 'loopfollow-a1b2c3d4e5f60718';

  afterEach(() => {
    for (const client of clients.splice(0)) client.disconnect();
    vi.unstubAllGlobals();
  });

  async function listening() {
    const http = createServer();
    const server = servingTenant(new SocketIOServer(http, {}, 'nocturne.run', null, SECRET, 'http://api.internal'));
    await server.start();
    await new Promise<void>((resolve) => http.listen(0, '127.0.0.1', resolve));
    return { server, port: (http.address() as AddressInfo).port };
  }

  /** Connects the way LoopFollow does: its token in the query, then an `authorize` carrying no credential. */
  async function loopFollow(port: number, token: string) {
    const client = connectClient(`http://127.0.0.1:${port}`, {
      transports: ['websocket'],
      reconnection: false,
      extraHeaders: { 'X-Forwarded-Host': 'rhys.nocturne.run' },
      query: { token },
    });
    clients.push(client);
    const updates: unknown[] = [];
    client.on('dataUpdate', (payload: unknown) => updates.push(payload));
    const disconnected = new Promise<void>((resolve) => client.once('disconnect', () => resolve()));
    await new Promise<void>((resolve) => client.once('connect_ack', () => resolve()));
    const authorized: unknown = await client.emitWithAck('authorize', { client: 'LoopFollow', history: 1 });
    return { client, updates, authorized, disconnected };
  }

  const settle = () => new Promise((resolve) => setTimeout(resolve, 50));

  it('authorizes on its query token and receives dataUpdate', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: () => Promise.resolve({ tenantRelay: true, categories: ['entries'], historyClamped: false }),
    });
    vi.stubGlobal('fetch', fetchMock);
    const { server, port } = await listening();

    const { updates, authorized } = await loopFollow(port, TOKEN);
    server.broadcastDataUpdate({ sgvs: [{ mgdl: 120 }] }, 'rhys');
    await settle();

    expect(authorized).toEqual(expect.objectContaining({ read: true }));
    expect(new URL(String(fetchMock.mock.calls[0][0])).searchParams.get('token')).toBe(TOKEN);
    expect(updates).toEqual([{ sgvs: [{ mgdl: 120 }] }]);
    await server.stop();
  });

  it('refuses a query token the API refuses and delivers nothing', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 401 }));
    const { server, port } = await listening();

    const { client, updates, authorized, disconnected } = await loopFollow(port, 'loopfollow-ffffffffffffffff');
    await disconnected;
    server.broadcastDataUpdate({ sgvs: [{ mgdl: 120 }] }, 'rhys');
    await settle();

    expect(authorized).toEqual(expect.objectContaining({ read: false }));
    expect(client.connected).toBe(false);
    expect(updates).toEqual([]);
    await server.stop();
  });
});

describe('SocketIOServer.handleAuthorize', () => {
  function pendingSocket(pendingTenantSlug?: string) {
    return {
      id: 'sock1',
      data: { pendingTenantSlug } as Record<string, unknown>,
      join: vi.fn(),
      disconnect: vi.fn(),
    };
  }

  function makeApiServer(apiBaseUrl = 'http://api.internal') {
    return servingTenant(new SocketIOServer(
      createServer(),
      {},
      'nocturne.run',
      null,
      SECRET,
      apiBaseUrl,
    ));
  }

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function admission(body: unknown) {
    return { ok: true, status: 200, json: () => Promise.resolve(body) };
  }

  it('joins the tenant room when the API admits the credential to it', async () => {
    const fetchMock = vi.fn().mockResolvedValue(admission({ tenantRelay: true }));
    vi.stubGlobal('fetch', fetchMock);

    const server = makeApiServer();
    const socket = pendingSocket('rhys');
    const callback = vi.fn();

    await server.handleAuthorize(socket as never, { secret: 'sha1hash' }, callback);

    expect(socket.join).toHaveBeenCalledWith('tenant:rhys');
    expect(socket.data.tenantSlug).toBe('rhys');
    expect(callback).toHaveBeenCalledWith(
      expect.objectContaining({ read: true }),
    );

    // The probe must carry the client's credential scoped to its own tenant, and
    // must NOT carry the bridge's instance key.
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toBe('http://api.internal/api/v4/me/realtime-admission');
    expect(init.headers['api-secret']).toBe('sha1hash');
    expect(init.headers['X-Forwarded-Host']).toBe('rhys.nocturne.run');
    expect(init.headers['X-Instance-Key']).toBeUndefined();
  });

  it.each([
    ['restricted', { tenantRelay: false }],
    ['silent on the tenant room', {}],
  ])('authorizes a credential the API admission leaves %s without joining it', async (_case, body) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(admission(body)));

    const server = makeApiServer();
    const socket = pendingSocket('rhys');
    const callback = vi.fn();

    await server.handleAuthorize(socket as never, { token: 'guest-token' }, callback);

    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.disconnect).not.toHaveBeenCalled();
    expect(socket.data.tenantSlug).toBe('rhys');
    expect(callback).toHaveBeenCalledWith(expect.objectContaining({ read: true }));
  });

  it('joins the category rooms the admission grants an unclamped credential', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        admission({ tenantRelay: true, categories: ['entries', 'care'], historyClamped: false }),
      ),
    );

    const server = makeApiServer();
    const socket = pendingSocket('rhys');

    await server.handleAuthorize(socket as never, { secret: 'sha1hash' });

    expect(socket.join).toHaveBeenCalledWith('tenant:rhys:category:entries');
    expect(socket.join).toHaveBeenCalledWith('tenant:rhys:category:care');
  });

  it.each([
    ['history-clamped', { tenantRelay: true, categories: ['entries'], historyClamped: true }],
    ['silent on the clamp', { tenantRelay: true, categories: ['entries'] }],
  ])('joins no category room for a credential the admission leaves %s', async (_case, body) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(admission(body)));

    const server = makeApiServer();
    const socket = pendingSocket('rhys');

    await server.handleAuthorize(socket as never, { secret: 'sha1hash' });

    expect(socket.join).toHaveBeenCalledWith('tenant:rhys');
    expect(socket.join.mock.calls.some(([room]) => String(room).includes(':category:'))).toBe(false);
  });

  const SUBJECT = '0a5f2c1e-1111-4222-8333-444455556666';

  it("joins the subject's room when the admission names a subject", async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(admission({ tenantRelay: true, subjectId: SUBJECT })));

    const server = makeApiServer();
    const socket = pendingSocket('rhys');

    await server.handleAuthorize(socket as never, { secret: 'sha1hash' });

    expect(socket.join.mock.calls.map(([room]) => room)).toEqual([
      'tenant:rhys',
      `tenant:rhys:subject:${SUBJECT}`,
    ]);
  });

  it('joins no room for a restricted credential that names a subject', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(admission({ tenantRelay: false, subjectId: SUBJECT })));

    const server = makeApiServer();
    const socket = pendingSocket('rhys');

    await server.handleAuthorize(socket as never, { token: 'guest-token' });

    expect(socket.data.tenantSlug).toBe('rhys');
    expect(socket.join).not.toHaveBeenCalled();
  });

  it('joins no subject room for a subject id not in canonical form', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(admission({ tenantRelay: true, subjectId: SUBJECT.toUpperCase() })),
    );

    const server = makeApiServer();
    const socket = pendingSocket('rhys');

    await server.handleAuthorize(socket as never, { secret: 'sha1hash' });

    expect(socket.join.mock.calls.map(([room]) => room)).toEqual(['tenant:rhys']);
  });

  it('passes a subject token through as a query parameter', async () => {
    const fetchMock = vi.fn().mockResolvedValue(admission({ tenantRelay: true }));
    vi.stubGlobal('fetch', fetchMock);

    const server = makeApiServer();
    const socket = pendingSocket('rhys');

    await server.handleAuthorize(socket as never, { token: 'phone-a1b2c3d4e5f6g7h8' });

    expect(String(fetchMock.mock.calls[0][0])).toContain('token=phone-a1b2c3d4e5f6g7h8');
    expect(socket.data.tenantSlug).toBe('rhys');
  });

  it('disconnects without joining when the API rejects the credential', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 401 }));

    const server = makeApiServer();
    const socket = pendingSocket('rhys');
    const callback = vi.fn();

    await server.handleAuthorize(socket as never, { secret: 'wrong' }, callback);

    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.data.tenantSlug).toBeUndefined();
    expect(socket.disconnect).toHaveBeenCalled();
    expect(callback).toHaveBeenCalledWith(
      expect.objectContaining({ read: false }),
    );
  });

  it('refuses an anonymous authorize the API admission refuses, as it does on a private tenant', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: false, status: 401 });
    vi.stubGlobal('fetch', fetchMock);

    const server = makeApiServer();
    const socket = pendingSocket('rhys');
    const callback = vi.fn();

    await server.handleAuthorize(socket as never, { client: 'LoopFollow', history: 1 }, callback);

    const [url, init] = fetchMock.mock.calls[0];
    expect(new URL(String(url)).searchParams.has('token')).toBe(false);
    expect(init.headers['api-secret']).toBeUndefined();
    expect(init.headers['Authorization']).toBeUndefined();
    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.data.tenantSlug).toBeUndefined();
    expect(socket.disconnect).toHaveBeenCalled();
    expect(callback).toHaveBeenCalledWith(expect.objectContaining({ read: false }));
  });

  it('grants an anonymous authorize the read the API admission gives it, and no room it does not', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(admission({ tenantRelay: false, categories: ['entries'] })));

    const server = makeApiServer();
    const socket = pendingSocket('rhys');
    const callback = vi.fn();

    await server.handleAuthorize(socket as never, {}, callback);

    expect(socket.data.tenantSlug).toBe('rhys');
    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.disconnect).not.toHaveBeenCalled();
    expect(callback).toHaveBeenCalledWith(expect.objectContaining({ read: true }));
  });

  it('presents the handshake query token when the authorize payload carries no credential', async () => {
    const fetchMock = vi.fn().mockResolvedValue(admission({ tenantRelay: true }));
    vi.stubGlobal('fetch', fetchMock);

    const server = makeApiServer();
    const socket = pendingSocket('rhys');
    socket.data.handshakeToken = 'loopfollow-a1b2c3d4e5f60718';

    await server.handleAuthorize(socket as never, { client: 'LoopFollow', history: 1 });

    expect(new URL(String(fetchMock.mock.calls[0][0])).searchParams.get('token')).toBe('loopfollow-a1b2c3d4e5f60718');
    expect(socket.join).toHaveBeenCalledWith('tenant:rhys');
    expect(socket.data.readmission).toEqual({ kind: 'credential', token: 'loopfollow-a1b2c3d4e5f60718' });
  });

  it('presents a secret the API refuses as an api-secret again as an access token', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({ ok: false, status: 401 })
      .mockResolvedValueOnce(admission({ tenantRelay: true }));
    vi.stubGlobal('fetch', fetchMock);

    const server = makeApiServer();
    const socket = pendingSocket('rhys');
    const callback = vi.fn();

    await server.handleAuthorize(socket as never, { secret: 'loopfollow-a1b2c3d4e5f60718' }, callback);

    expect(fetchMock).toHaveBeenCalledTimes(2);
    const [url, init] = fetchMock.mock.calls[1];
    expect(new URL(String(url)).searchParams.get('token')).toBe('loopfollow-a1b2c3d4e5f60718');
    expect(init.headers['api-secret']).toBeUndefined();
    expect(socket.join).toHaveBeenCalledWith('tenant:rhys');
    expect(socket.data.readmission).toEqual({ kind: 'credential', token: 'loopfollow-a1b2c3d4e5f60718' });
    expect(callback).toHaveBeenCalledWith(expect.objectContaining({ read: true }));
  });

  it('presents a secret the API accepts as an api-secret only once', async () => {
    const fetchMock = vi.fn().mockResolvedValue(admission({ tenantRelay: true }));
    vi.stubGlobal('fetch', fetchMock);

    const server = makeApiServer();
    const socket = pendingSocket('rhys');

    await server.handleAuthorize(socket as never, { secret: 'sha1hash' });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(new URL(String(fetchMock.mock.calls[0][0])).searchParams.has('token')).toBe(false);
  });

  it('does not present a secret again while the API gives no answer', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: false, status: 503 });
    vi.stubGlobal('fetch', fetchMock);

    const server = makeApiServer();
    const socket = pendingSocket('rhys');

    await server.handleAuthorize(socket as never, { secret: 'loopfollow-a1b2c3d4e5f60718' });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(socket.disconnect).toHaveBeenCalled();
  });

  it('refuses a token the API refuses in both forms', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 401 }));

    const server = makeApiServer();
    const socket = pendingSocket('rhys');
    const callback = vi.fn();

    await server.handleAuthorize(socket as never, { secret: 'not-a-token' }, callback);

    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.data.tenantSlug).toBeUndefined();
    expect(socket.disconnect).toHaveBeenCalled();
    expect(callback).toHaveBeenCalledWith(expect.objectContaining({ read: false }));
  });

  it('refuses to authorize a socket whose host resolved to no tenant', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    const server = makeApiServer();
    const socket = pendingSocket(undefined);

    await server.handleAuthorize(socket as never, { secret: 'sha1hash' });

    expect(fetchMock).not.toHaveBeenCalled();
    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.disconnect).toHaveBeenCalled();
  });

  it('does not re-probe a socket already authorized by a ticket', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    const server = makeApiServer();
    const socket = pendingSocket();
    socket.data.tenantSlug = 'rhys';
    const callback = vi.fn();

    await server.handleAuthorize(socket as never, { secret: 'sha1hash' }, callback);

    expect(fetchMock).not.toHaveBeenCalled();
    expect(socket.disconnect).not.toHaveBeenCalled();
    expect(callback).toHaveBeenCalledWith(expect.objectContaining({ read: true }));
  });
});

describe('SocketIOServer tracker broadcast fan-out', () => {
  async function startedServer() {
    const server = servingTenant(new SocketIOServer(
      createServer(),
      {},
      'nocturne.run',
      null,
      SECRET,
      'http://api.internal',
    ));
    await server.start();
    return server;
  }

  it('re-emits a trackerUpdate to the tenant room', async () => {
    const server = await startedServer();

    const emitted: { room: string; event: string; payload: unknown }[] = [];
    vi.spyOn(server.getIO()!, 'to').mockImplementation(
      (room: string) =>
        ({
          emit: (event: string, payload: unknown) => emitted.push({ room, event, payload }),
        }) as never,
    );

    const payload = { action: 'create', instance: { id: 'tracker-1' } };
    server.broadcastTrackerUpdate(payload, 'rhys');

    expect(emitted).toEqual([{ room: 'tenant:rhys', event: 'trackerUpdate', payload }]);
    server.getIO()!.close();
  });

  it('re-emits a trackerDefinitionUpdate to the tenant room', async () => {
    const server = await startedServer();

    const emitted: { room: string; event: string; payload: unknown }[] = [];
    vi.spyOn(server.getIO()!, 'to').mockImplementation(
      (room: string) =>
        ({
          emit: (event: string, payload: unknown) => emitted.push({ room, event, payload }),
        }) as never,
    );

    const payload = { definitionId: 'definition-1' };
    server.broadcastTrackerDefinitionUpdate(payload, 'rhys');

    expect(emitted).toEqual([{ room: 'tenant:rhys', event: 'trackerDefinitionUpdate', payload }]);
    server.getIO()!.close();
  });

  it('refuses to broadcast a trackerDefinitionUpdate without a tenant slug', async () => {
    const server = await startedServer();
    const toSpy = vi.spyOn(server.getIO()!, 'to');

    server.broadcastTrackerDefinitionUpdate({ definitionId: 'definition-1' });

    expect(toSpy).not.toHaveBeenCalled();
    server.getIO()!.close();
  });

  it('refuses to broadcast a trackerUpdate without a tenant slug', async () => {
    const server = await startedServer();
    const toSpy = vi.spyOn(server.getIO()!, 'to');

    server.broadcastTrackerUpdate({ action: 'create', instance: { id: 'tracker-1' } });

    expect(toSpy).not.toHaveBeenCalled();
    server.getIO()!.close();
  });
});

describe('SocketIOServer in-app notification fan-out', () => {
  const SUBJECT_A = '0a5f2c1e-1111-4222-8333-444455556666';

  async function startedServer() {
    const server = servingTenant(new SocketIOServer(
      createServer(),
      {},
      'nocturne.run',
      null,
      SECRET,
      'http://api.internal',
    ));
    await server.start();
    return server;
  }

  it("routes a notification to the recipient's subject room and no other", async () => {
    const server = await startedServer();
    const emitted: { room: string; event: string; payload: unknown }[] = [];
    vi.spyOn(server.getIO()!, 'to').mockImplementation(
      (room: string) =>
        ({
          emit: (event: string, payload: unknown) => emitted.push({ room, event, payload }),
        }) as never,
    );

    const payload = { id: 'notification-1' };
    server.broadcastInAppNotification('notificationCreated', payload, 'rhys', SUBJECT_A);

    // Member B's socket is in tenant:rhys:subject:B, so it is never a target here.
    expect(emitted).toEqual([
      {
        room: `tenant:rhys:subject:${SUBJECT_A}`,
        event: 'notificationCreated',
        payload,
      },
    ]);
    server.getIO()!.close();
  });

  it('drops a relayed notification that names no recipient', async () => {
    const server = await startedServer();
    const toSpy = vi.spyOn(server.getIO()!, 'to');

    server.broadcastInAppNotification('notificationCreated', { id: 'notification-1' }, 'rhys');

    expect(toSpy).not.toHaveBeenCalled();
    server.getIO()!.close();
  });
});

// ---------------------------------------------------------------------------
// v3 /storage namespace — AAPS / xDrip+ / NightGuard uploaders
// ---------------------------------------------------------------------------

describe('SocketIOServer v3 /storage namespace', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function makeStorageServer(apiBaseUrl = 'http://api.internal') {
    return servingTenant(new SocketIOServer(createServer(), {}, 'nocturne.run', null, SECRET, apiBaseUrl));
  }

  /** A fake socket carrying a pending tenant slug (post-handshake state). */
  function storageSocket(pendingTenantSlug?: string) {
    return {
      id: 'storage-1',
      data: { pendingTenantSlug } as Record<string, unknown>,
      join: vi.fn(),
      disconnect: vi.fn(),
    };
  }

  it('subscribes for the requested collections when the token is valid', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, status: 200 }));
    const server = makeStorageServer();
    const socket = storageSocket('rhys');
    const ack = vi.fn();

    await server.handleStorageSubscribe(
      socket as never,
      { accessToken: 'aaps-token', collections: ['entries', 'devicestatus', 'profile'] },
      ack,
    );

    expect(ack).toHaveBeenCalledWith({ success: true, collections: ['entries', 'devicestatus', 'profile'] });
    expect(socket.join).toHaveBeenCalledWith('storage:rhys:entries');
    expect(socket.join).toHaveBeenCalledWith('storage:rhys:devicestatus');
    expect(socket.join).toHaveBeenCalledWith('storage:rhys:profile');
    expect(socket.data.tenantSlug).toBe('rhys');
    expect(socket.data.pendingTenantSlug).toBeUndefined();
  });

  it('defaults to all known collections when none are requested', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, status: 200 }));
    const server = makeStorageServer();
    const socket = storageSocket('rhys');
    const ack = vi.fn();

    await server.handleStorageSubscribe(socket as never, { accessToken: 'tok' }, ack);

    expect(ack).toHaveBeenCalledWith({
      success: true,
      collections: ['devicestatus', 'entries', 'profile', 'treatments', 'food', 'settings'],
    });
    expect(socket.join).toHaveBeenCalledTimes(6);
  });

  it.each([
    [['food'], ['food']],
    [['foods'], ['foods']],
    [['food', 'entries'], ['food', 'entries']],
    [['foods', 'food'], ['foods']],
    [['food', 'foods'], ['food']],
  ])('subscribes %j, acks %j and joins only those rooms', async (requested, acked) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, status: 200 }));
    const server = makeStorageServer();
    const socket = storageSocket('rhys');
    const ack = vi.fn();

    await server.handleStorageSubscribe(socket as never, { accessToken: 'tok', collections: requested }, ack);

    expect(ack).toHaveBeenCalledWith({ success: true, collections: acked });
    expect(socket.join.mock.calls.map(([room]) => room)).toEqual(acked.map((name) => `storage:rhys:${name}`));
  });

  it('authorises food against the food read endpoint', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: false, status: 403 });
    vi.stubGlobal('fetch', fetchMock);
    const server = makeStorageServer();
    const socket = storageSocket('rhys');

    await server.handleStorageSubscribe(socket as never, { accessToken: 'tok', collections: ['food'] });

    expect(String(fetchMock.mock.calls[0][0])).toContain('/api/v3/food');
    expect(socket.disconnect).toHaveBeenCalledWith(true);
  });

  it('filters out unknown collection names', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, status: 200 }));
    const server = makeStorageServer();
    const socket = storageSocket('rhys');

    await server.handleStorageSubscribe(
      socket as never,
      { accessToken: 'tok', collections: ['entries', 'bogus', '<script>'] },
    );

    expect(socket.join).toHaveBeenCalledTimes(1);
    expect(socket.join).toHaveBeenCalledWith('storage:rhys:entries');
  });

  it('disconnects on auth failure so subscribe is not a credential-guessing oracle', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 401 }));
    const server = makeStorageServer();
    const socket = storageSocket('rhys');
    const ack = vi.fn();

    await server.handleStorageSubscribe(
      socket as never,
      { accessToken: 'wrong', collections: ['entries'] },
      ack,
    );

    expect(ack).toHaveBeenCalledWith({ success: false, message: 'Unauthorized to receive any collection' });
    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.disconnect).toHaveBeenCalledWith(true);
    expect(socket.data.tenantSlug).toBeUndefined();
  });

  it('disconnects when no accessToken is supplied', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const server = makeStorageServer();
    const socket = storageSocket('rhys');
    const ack = vi.fn();

    await server.handleStorageSubscribe(socket as never, { collections: ['entries'] }, ack);

    expect(fetchMock).not.toHaveBeenCalled();
    expect(ack).toHaveBeenCalledWith({ success: false, message: 'Missing or bad accessToken' });
    expect(socket.disconnect).toHaveBeenCalledWith(true);
  });

  it('disconnects when the host resolved to no tenant', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const server = makeStorageServer();
    const socket = storageSocket(undefined);
    const ack = vi.fn();

    await server.handleStorageSubscribe(socket as never, { accessToken: 'tok' }, ack);

    expect(fetchMock).not.toHaveBeenCalled();
    expect(ack).toHaveBeenCalledWith({ success: false, message: 'no resolvable tenant' });
    expect(socket.disconnect).toHaveBeenCalledWith(true);
  });

  it('probes each collection against its own v3 endpoint, not a single blanket probe', async () => {
    // entries is authorized, treatments is not. Only entries should be joined.
    const fetchMock = vi.fn().mockImplementation((url: URL | string) => {
      const s = String(url);
      if (s.includes('/api/v3/entries')) return Promise.resolve({ ok: true, status: 200 });
      return Promise.resolve({ ok: false, status: 403 });
    });
    vi.stubGlobal('fetch', fetchMock);
    const server = makeStorageServer();
    const socket = storageSocket('rhys');
    const ack = vi.fn();

    await server.handleStorageSubscribe(
      socket as never,
      { accessToken: 'tok', collections: ['entries', 'treatments'] },
      ack,
    );

    // Only the authorized collection appears in the ack and room join.
    expect(ack).toHaveBeenCalledWith({ success: true, collections: ['entries'] });
    expect(socket.join).toHaveBeenCalledTimes(1);
    expect(socket.join).toHaveBeenCalledWith('storage:rhys:entries');
    // Both endpoints were probed — per-collection auth, not blanket.
    expect(fetchMock).toHaveBeenCalledTimes(2);
    const probedPaths = fetchMock.mock.calls.map(([u]) => String(u));
    expect(probedPaths.some((p) => p.includes('/api/v3/entries'))).toBe(true);
    expect(probedPaths.some((p) => p.includes('/api/v3/treatments'))).toBe(true);
  });

  it('probes the requested collections concurrently', async () => {
    const answers: Array<(response: unknown) => void> = [];
    const fetchMock = vi.fn().mockImplementation(
      () => new Promise((resolve) => answers.push(resolve)),
    );
    vi.stubGlobal('fetch', fetchMock);
    const server = makeStorageServer();
    const socket = storageSocket('rhys');
    const ack = vi.fn();

    const subscribing = server.handleStorageSubscribe(
      socket as never,
      { accessToken: 'aaps-token', collections: ['entries', 'treatments', 'devicestatus'] },
      ack,
    );
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));
    for (const answer of answers) answer({ ok: true, status: 200 });
    await subscribing;

    expect(ack).toHaveBeenCalledWith({ success: true, collections: ['entries', 'treatments', 'devicestatus'] });
  });

  it('probes the v3 endpoint with a Bearer token scoped to the tenant', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200 });
    vi.stubGlobal('fetch', fetchMock);
    const server = makeStorageServer();
    const socket = storageSocket('rhys');

    await server.handleStorageSubscribe(
      socket as never,
      { accessToken: 'aaps-token', collections: ['entries'] },
    );

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/api/v3/entries');
    expect([...new URL(String(url)).searchParams]).toEqual([['limit', '1']]);
    expect(init.headers['Authorization']).toBe('Bearer aaps-token');
    expect(init.headers['X-Forwarded-Host']).toBe('rhys.nocturne.run');
    // The bridge must NOT use its instance key to authorize the client.
    expect(init.headers['X-Instance-Key']).toBeUndefined();
    expect(init.headers['X-Instance-Service']).toBeUndefined();
  });
});

describe('SocketIOServer storage category fan-out', () => {
  const clients: ClientSocket[] = [];

  afterEach(() => {
    for (const client of clients.splice(0)) client.disconnect();
  });

  async function listening() {
    const http = createServer();
    const server = servingTenant(new SocketIOServer(http, {}, 'nocturne.run', null, SECRET));
    await server.start();
    await new Promise<void>((resolve) => http.listen(0, '127.0.0.1', resolve));
    return { server, port: (http.address() as AddressInfo).port };
  }

  /** A browser socket admitted with `ticket`, recording every storage event it receives. */
  async function browser(port: number, ticket: RealtimeGrant) {
    const client = connectClient(`http://127.0.0.1:${port}`, {
      transports: ['websocket'],
      extraHeaders: { 'X-Forwarded-Host': 'rhys.nocturne.run' },
      auth: { token: signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, ticket) },
    });
    clients.push(client);
    const received: { event: string; payload: unknown }[] = [];
    for (const event of ['create', 'update', 'delete']) {
      client.on(event, (payload: unknown) => received.push({ event, payload }));
    }
    await new Promise<void>((resolve) => client.once('connect_ack', () => resolve()));
    return received;
  }

  /** Lets in-flight emits land, so an absent event is absent rather than late. */
  const settle = () => new Promise((resolve) => setTimeout(resolve, 50));

  const bolus = { colName: 'care', recordType: 'bolus', doc: { id: 'b1', insulin: 2 } };

  it('delivers a v4 care event to a member granted care and not to one without it', async () => {
    const { server, port } = await listening();
    try {
      const treatmentsReader = await browser(port, grant(true, { categories: ['entries', 'care'] }));
      const glucoseOnly = await browser(port, grant(true, { categories: ['entries', 'glucose'] }));

      server.broadcastStorageEvent('create', bolus, 'rhys');

      await vi.waitFor(() => expect(treatmentsReader).toEqual([{ event: 'create', payload: bolus }]));
      await settle();
      expect(glucoseOnly).toEqual([]);
    } finally {
      await server.stop();
    }
  });

  it('delivers no storage event to a restricted or history-clamped socket, whatever its grant names', async () => {
    const { server, port } = await listening();
    try {
      const share = await browser(port, grant(false, { categories: ['entries', 'care'] }));
      const clamped = await browser(port, grant(true, { categories: ['entries', 'care'], historyClamped: true }));

      server.broadcastStorageEvent('create', bolus, 'rhys');
      server.broadcastStorageEvent('create', { colName: 'entries', doc: { sgv: 120 } }, 'rhys');

      await settle();
      expect(share).toEqual([]);
      expect(clamped).toEqual([]);
    } finally {
      await server.stop();
    }
  });

  it('delivers a legacy or v4 event only to the sockets granted its category', async () => {
    const { server, port } = await listening();
    try {
      const glucoseOnly = await browser(port, grant(true, { categories: ['entries'] }));
      const entry = { colName: 'entries', doc: { sgv: 120 } };

      server.broadcastStorageEvent('create', { colName: 'treatments', doc: { _id: 't1' } }, 'rhys');
      server.broadcastStorageEvent('create', { colName: 'device', recordType: 'apsSnapshot', doc: { id: 'a1' } }, 'rhys');
      server.broadcastStorageEvent('create', entry, 'rhys');

      await vi.waitFor(() => expect(glucoseOnly).toEqual([{ event: 'create', payload: entry }]));
      await settle();
      expect(glucoseOnly).toHaveLength(1);
    } finally {
      await server.stop();
    }
  });

  it('delivers an event naming no category to no browser socket', async () => {
    const { server, port } = await listening();
    try {
      const member = await browser(port, grant(true, { categories: ['entries', 'care'] }));

      server.broadcastStorageEvent('create', { doc: { id: 'x' } }, 'rhys');

      await settle();
      expect(member).toEqual([]);
    } finally {
      await server.stop();
    }
  });
});

describe('SocketIOServer v3 /storage broadcast fan-out', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  /** Build a started server with a real socket.io Namespace so we can assert
   *  on the actual emit target and payload via the BroadcastingWildcard. */
  async function startedServer() {
    const server = servingTenant(new SocketIOServer(
      createServer(),
      {},
      'nocturne.run',
      null,
      SECRET,
      'http://api.internal',
    ));
    await server.start();
    return server;
  }

  it('fans a create event out to the /storage collection room', async () => {
    const server = await startedServer();
    const storageNsp = server.getIO()!.of('/storage');
    const toSpy = vi.spyOn(storageNsp, 'to');

    server.broadcastStorageEvent('create', { colName: 'entries', doc: { srvModified: 123 } }, 'rhys');

    expect(toSpy).toHaveBeenCalledWith('storage:rhys:entries');
    server.getIO()!.close();
  });

  it('amends the payload colName to the v3-client spelling (profiles -> profile)', async () => {
    const server = await startedServer();
    const storageNsp = server.getIO()!.of('/storage');

    // The fan-out calls storageNsp.to(room).emit(event, payload). Capture the
    // payload by stubbing .to() to return a recording fake.
    const emitted: { event: string; payload: unknown }[] = [];
    vi.spyOn(storageNsp, 'to').mockReturnValue({
      emit: (event: string, payload: unknown) => emitted.push({ event, payload }),
    } as never);

    server.broadcastStorageEvent('update', { colName: 'profiles', doc: { srvModified: 1 } }, 'rhys');

    expect(emitted).toHaveLength(1);
    expect(emitted[0].event).toBe('update');
    expect((emitted[0].payload as { colName: string }).colName).toBe('profile');
    server.getIO()!.close();
  });

  it.each(['food', 'foods'])('delivers a %s broadcast to each spelling room under that spelling', async (broadcast) => {
    const server = await startedServer();
    const storageNsp = server.getIO()!.of('/storage');

    const emitted: { room: string; payload: { colName: string } }[] = [];
    vi.spyOn(storageNsp, 'to').mockImplementation(((room: string) => ({
      emit: (_event: string, payload: { colName: string }) => emitted.push({ room, payload }),
    })) as never);

    server.broadcastStorageEvent('create', { colName: broadcast, doc: {} }, 'rhys');

    expect(emitted).toEqual([
      { room: 'storage:rhys:food', payload: { colName: 'food', doc: {} } },
      { room: 'storage:rhys:foods', payload: { colName: 'foods', doc: {} } },
    ]);
    server.getIO()!.close();
  });

  it('does not fan out to /storage without a tenant slug', async () => {
    const server = await startedServer();
    const storageNsp = server.getIO()!.of('/storage');
    const toSpy = vi.spyOn(storageNsp, 'to');

    // No tenant slug → emitTarget returns null → early return before fan-out.
    server.broadcastStorageEvent('create', { colName: 'entries', doc: {} });

    expect(toSpy).not.toHaveBeenCalled();
    server.getIO()!.close();
  });
});

// ---------------------------------------------------------------------------
// v3 /alarm namespace
// ---------------------------------------------------------------------------

describe('SocketIOServer v3 /alarm namespace', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function makeAlarmServer(apiBaseUrl = 'http://api.internal') {
    return servingTenant(new SocketIOServer(createServer(), {}, 'nocturne.run', null, SECRET, apiBaseUrl));
  }

  function alarmSocket(pendingTenantSlug?: string) {
    return {
      id: 'alarm-1',
      data: { pendingTenantSlug } as Record<string, unknown>,
      join: vi.fn(),
      disconnect: vi.fn(),
    };
  }

  function admission(body: unknown) {
    return { ok: true, status: 200, json: () => Promise.resolve(body) };
  }

  it('subscribes and joins when the API admits the token to the tenant relay', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(admission({ tenantRelay: true })));
    const server = makeAlarmServer();
    const socket = alarmSocket('rhys');
    const ack = vi.fn();

    await server.handleAlarmSubscribe(socket as never, { accessToken: 'tok' }, ack);

    expect(ack).toHaveBeenCalledWith({ success: true, message: 'Subscribed for alarms' });
    expect(socket.join).toHaveBeenCalledWith('alarm:rhys');
    expect(socket.data.tenantSlug).toBe('rhys');
  });

  it('sends a socket that subscribes after an alarm was raised the alarm still sounding', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(admission({ tenantRelay: true })));
    const server = makeAlarmServer();
    const sounding = { level: 2, title: 'Urgent low', group: '0192a3b4-0000-7000-8000-000000000001' };
    const currentAlarm = vi.fn().mockResolvedValue({ event: 'urgent_alarm', alarm: sounding });
    server.currentAlarm = currentAlarm;
    const socket = { ...alarmSocket('rhys'), emit: vi.fn() };
    const ack = vi.fn();

    await server.handleAlarmSubscribe(socket as never, { accessToken: 'aaps' }, ack);

    expect(currentAlarm).toHaveBeenCalledExactlyOnceWith('rhys');
    expect(socket.emit).toHaveBeenCalledExactlyOnceWith('urgent_alarm', sounding);
    expect(ack.mock.invocationCallOrder[0]).toBeLessThan(socket.emit.mock.invocationCallOrder[0]);
  });

  it.each([
    ['nothing sounds', () => Promise.resolve(null)],
    ['the API cannot say', () => Promise.reject(new Error('AlarmHub down'))],
  ])('sends a new subscriber no alarm when %s', async (_case, currentAlarm) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(admission({ tenantRelay: true })));
    const server = makeAlarmServer();
    server.currentAlarm = currentAlarm;
    const socket = { ...alarmSocket('rhys'), emit: vi.fn() };
    const ack = vi.fn();

    await server.handleAlarmSubscribe(socket as never, { accessToken: 'aaps' }, ack);

    expect(ack).toHaveBeenCalledWith({ success: true, message: 'Subscribed for alarms' });
    expect(socket.emit).not.toHaveBeenCalled();
  });

  it('asks for no alarm for a socket it refuses', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(admission({ tenantRelay: false })));
    const server = makeAlarmServer();
    const currentAlarm = vi.fn().mockResolvedValue(null);
    server.currentAlarm = currentAlarm;

    await server.handleAlarmSubscribe(alarmSocket('rhys') as never, { accessToken: 'guest' });

    expect(currentAlarm).not.toHaveBeenCalled();
  });

  it('denies a restricted credential the API will not admit to the tenant relay', async () => {
    // An AAPS guest-style token can read entries, but it holds single
    // categories and must not receive the tenant's alarms.
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(admission({ tenantRelay: false })));
    const server = makeAlarmServer();
    const socket = alarmSocket('rhys');
    const ack = vi.fn();

    await server.handleAlarmSubscribe(socket as never, { accessToken: 'restricted' }, ack);

    expect(ack).toHaveBeenCalledWith({ success: false, message: 'Missing or bad accessToken' });
    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.disconnect).toHaveBeenCalledWith(true);
    expect(socket.data.tenantSlug).toBeUndefined();
  });

  it('denies when the admission probe throws', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('network down')));
    const server = makeAlarmServer();
    const socket = alarmSocket('rhys');
    const ack = vi.fn();

    await server.handleAlarmSubscribe(socket as never, { accessToken: 'tok' }, ack);

    expect(ack).toHaveBeenCalledWith({ success: false, message: 'Missing or bad accessToken' });
    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.disconnect).toHaveBeenCalledWith(true);
  });

  it('probes the admission endpoint with a Bearer token, never the entries endpoint', async () => {
    const fetchMock = vi.fn().mockResolvedValue(admission({ tenantRelay: true }));
    vi.stubGlobal('fetch', fetchMock);
    const server = makeAlarmServer();
    const socket = alarmSocket('rhys');

    await server.handleAlarmSubscribe(socket as never, { accessToken: 'aaps-token' });

    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toBe('http://api.internal/api/v4/me/realtime-admission');
    expect(String(url)).not.toContain('/api/v3/entries');
    expect(init.headers['Authorization']).toBe('Bearer aaps-token');
    expect(init.headers['X-Forwarded-Host']).toBe('rhys.nocturne.run');
    // The bridge must NOT use its instance key to authorize the client.
    expect(init.headers['X-Instance-Key']).toBeUndefined();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('disconnects on auth failure so subscribe is not a credential-guessing oracle', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 401 }));
    const server = makeAlarmServer();
    const socket = alarmSocket('rhys');
    const ack = vi.fn();

    await server.handleAlarmSubscribe(socket as never, { accessToken: 'wrong' }, ack);

    expect(ack).toHaveBeenCalledWith({ success: false, message: 'Missing or bad accessToken' });
    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.disconnect).toHaveBeenCalledWith(true);
  });

  it('disconnects when no accessToken is supplied', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const server = makeAlarmServer();
    const socket = alarmSocket('rhys');
    const ack = vi.fn();

    await server.handleAlarmSubscribe(socket as never, {}, ack);

    expect(fetchMock).not.toHaveBeenCalled();
    expect(ack).toHaveBeenCalledWith({ success: false, message: 'Missing or bad accessToken' });
    expect(socket.disconnect).toHaveBeenCalledWith(true);
  });

  it('disconnects when the host resolved to no tenant', async () => {
    const server = makeAlarmServer();
    const socket = alarmSocket(undefined);
    const ack = vi.fn();

    await server.handleAlarmSubscribe(socket as never, { accessToken: 'tok' }, ack);

    expect(ack).toHaveBeenCalledWith({ success: false, message: 'no resolvable tenant' });
    expect(socket.disconnect).toHaveBeenCalledWith(true);
  });

  it('forwards an ack from a subscriber whose credential may silence alarms', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(admission({ tenantRelay: true, mayAck: true })));
    const server = makeAlarmServer();
    const onAlarmAck = vi.fn();
    server.onAlarmAck = onAlarmAck;
    const socket = alarmSocket('rhys');
    await server.handleAlarmSubscribe(socket as never, { accessToken: 'caregiver' });

    server.handleAlarmAck(socket as never, 2, 'excursion-id', '1800000');

    expect(onAlarmAck).toHaveBeenCalledExactlyOnceWith('rhys', 2, 'excursion-id', 1800000);
  });

  it.each([
    ['says it may not', { tenantRelay: true, mayAck: false }],
    ['does not say', { tenantRelay: true }],
  ])('drops an ack from a subscriber whose admission %s silence alarms', async (_case, body) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(admission(body)));
    const server = makeAlarmServer();
    const onAlarmAck = vi.fn();
    server.onAlarmAck = onAlarmAck;
    const socket = alarmSocket('rhys');
    await server.handleAlarmSubscribe(socket as never, { accessToken: 'follower' });

    server.handleAlarmAck(socket as never, 2, 'excursion-id', 1800000);

    expect(onAlarmAck).not.toHaveBeenCalled();
  });
});

describe('SocketIOServer v3 /alarm broadcast fan-out', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  async function startedServer() {
    const server = servingTenant(new SocketIOServer(
      createServer(),
      {},
      'nocturne.run',
      null,
      SECRET,
      'http://api.internal',
    ));
    await server.start();
    return server;
  }

  function relayTo(server: SocketIOServer) {
    const io = server.getIO()!;
    const tenantEmit = vi.fn();
    const alarmEmit = vi.fn();
    const tenantTo = vi.spyOn(io, 'to').mockReturnValue({ emit: tenantEmit } as never);
    const alarmTo = vi.spyOn(io.of('/alarm'), 'to').mockReturnValue({ emit: alarmEmit } as never);
    const expectRooms = () => {
      expect(tenantTo).toHaveBeenCalledExactlyOnceWith('tenant:rhys');
      expect(alarmTo).toHaveBeenCalledExactlyOnceWith('alarm:rhys');
    };
    return {
      translator: new MessageTranslator(server, 'rhys'),
      tenantEmit,
      alarmEmit,
      expectRooms,
    };
  }

  const urgentNotification = {
    level: 2,
    title: 'Urgent High',
    message: 'BG 15.1',
    group: 'default',
    plugin: 'simplealarms',
    timestamp: 1700000000000,
    clear: false,
  };

  it('relays an urgent notification as urgent_alarm with its numeric level on both namespaces', async () => {
    const server = await startedServer();
    const { translator, tenantEmit, alarmEmit, expectRooms } = relayTo(server);

    translator.handleAlarm('urgent_alarm', urgentNotification);

    for (const emit of [tenantEmit, alarmEmit]) {
      expect(emit).toHaveBeenCalledExactlyOnceWith(
        'urgent_alarm',
        expect.objectContaining({ level: 2, title: 'Urgent High', group: 'default' }),
      );
    }
    expectRooms();
    server.getIO()!.close();
  });

  it('relays a warn notification as alarm with its numeric level on both namespaces', async () => {
    const server = await startedServer();
    const { translator, tenantEmit, alarmEmit, expectRooms } = relayTo(server);

    translator.handleAlarm('alarm', { ...urgentNotification, level: 1, title: 'Warning High' });

    for (const emit of [tenantEmit, alarmEmit]) {
      expect(emit).toHaveBeenCalledExactlyOnceWith(
        'alarm',
        expect.objectContaining({ level: 1, title: 'Warning High' }),
      );
    }
    expectRooms();
    server.getIO()!.close();
  });

  it('gives a level-less alarm the numeric level of the event it arrived on', async () => {
    const server = await startedServer();
    const { translator, tenantEmit } = relayTo(server);

    translator.handleAlarm('urgent_alarm', { title: 'No level' });
    translator.handleAlarm('alarm', { title: 'No level' });

    expect(tenantEmit).toHaveBeenNthCalledWith(1, 'urgent_alarm', expect.objectContaining({ level: 2 }));
    expect(tenantEmit).toHaveBeenNthCalledWith(2, 'alarm', expect.objectContaining({ level: 1 }));
    server.getIO()!.close();
  });

  const engineAlarm = {
    level: 2,
    title: 'Urgent low',
    message: 'BG Now: 3.0 mmol/L',
    group: '0192a3b4-0000-7000-8000-000000000001',
    plugin: 'threshold',
    eventName: 'threshold',
    timestamp: 1700000000000,
    clear: false,
  };

  it('relays an engine alarm to /alarm only, never the web tenant room', async () => {
    const server = await startedServer();
    const { translator, tenantEmit, alarmEmit } = relayTo(server);

    translator.handleEngineAlarm('urgent_alarm', engineAlarm);
    translator.handleEngineAlarm('alarm', { ...engineAlarm, level: 1 });

    expect(alarmEmit).toHaveBeenNthCalledWith(1, 'urgent_alarm', expect.objectContaining(engineAlarm));
    expect(alarmEmit).toHaveBeenNthCalledWith(2, 'alarm', expect.objectContaining({ level: 1, group: engineAlarm.group }));
    expect(tenantEmit).not.toHaveBeenCalled();
    server.getIO()!.close();
  });

  it('relays an engine all-clear to /alarm only, never the web tenant room', async () => {
    const server = await startedServer();
    const { translator, tenantEmit, alarmEmit } = relayTo(server);
    const clear = { clear: true, title: 'All Clear', level: 0, group: 'default' };

    translator.handleEngineClearAlarm(clear);

    expect(alarmEmit).toHaveBeenCalledExactlyOnceWith('clear_alarm', clear);
    expect(tenantEmit).not.toHaveBeenCalled();
    server.getIO()!.close();
  });

  it('relays clear_alarm with the clearing notification on both namespaces', async () => {
    const server = await startedServer();
    const { translator, tenantEmit, alarmEmit, expectRooms } = relayTo(server);
    const clear = {
      clear: true,
      title: 'Alarm Cleared - default',
      message: 'Level 2 alarm has been acknowledged and cleared',
      group: 'default',
      level: 2,
    };

    translator.handleClearAlarm(clear);

    expect(tenantEmit).toHaveBeenCalledExactlyOnceWith('clear_alarm', clear);
    expect(alarmEmit).toHaveBeenCalledExactlyOnceWith('clear_alarm', clear);
    expectRooms();
    server.getIO()!.close();
  });

  const apiAnnouncement = {
    level: 0,
    title: 'Sensor change',
    message: 'Change the sensor',
    group: 'default',
    plugin: 'announcement',
    timestamp: 1700000000000,
    isAnnouncement: true,
  };

  it('relays an announcement with its numeric level to the tenant room and /alarm', async () => {
    const server = await startedServer();
    const { translator, tenantEmit, alarmEmit, expectRooms } = relayTo(server);

    translator.handleAnnouncement(apiAnnouncement);

    for (const emit of [tenantEmit, alarmEmit]) {
      expect(emit).toHaveBeenCalledExactlyOnceWith(
        'announcement',
        expect.objectContaining({ level: 0, title: 'Sensor change', isAnnouncement: true }),
      );
    }
    expectRooms();
    server.getIO()!.close();
  });

  it('relays a notification with its numeric level to /alarm', async () => {
    const server = await startedServer();
    const { translator, alarmEmit } = relayTo(server);

    translator.handleNotification({ ...apiAnnouncement, level: 1, isAnnouncement: false });

    expect(alarmEmit).toHaveBeenCalledExactlyOnceWith(
      'notification',
      expect.objectContaining({ level: 1, title: 'Sensor change', group: 'default' }),
    );
    server.getIO()!.close();
  });

  it('keeps relaying a notification to the tenant room', async () => {
    const server = await startedServer();
    const { translator, tenantEmit, expectRooms } = relayTo(server);

    translator.handleNotification({ ...apiAnnouncement, level: 1, isAnnouncement: false });

    expect(tenantEmit).toHaveBeenCalledExactlyOnceWith(
      'notification',
      expect.objectContaining({ level: 1, title: 'Sensor change' }),
    );
    expectRooms();
    server.getIO()!.close();
  });
});

describe('a share host handshake', () => {
  const clients: ClientSocket[] = [];
  const host = 'abc123.share.nocturne.run';

  afterEach(() => {
    for (const client of clients.splice(0)) client.disconnect();
    vi.unstubAllGlobals();
  });

  it.each(['/', '/storage', '/alarm'])(
    'is refused on %s before any tenant list read or API probe',
    async (namespace) => {
      const fetchMock = vi.fn();
      vi.stubGlobal('fetch', fetchMock);
      const http = createServer();
      const server = new SocketIOServer(http, {}, 'nocturne.run', null, SECRET, 'http://api.internal');
      const isCurrentTenant = vi.fn(async () => true);
      const holdsTenantNow = vi.fn(() => true);
      const onApexUnresolved = vi.fn(async () => {});
      const onTenantAdmitted = vi.fn();
      server.isCurrentTenant = isCurrentTenant;
      server.holdsTenantNow = holdsTenantNow;
      server.onApexUnresolved = onApexUnresolved;
      server.onTenantAdmitted = onTenantAdmitted;
      await server.start();
      await new Promise<void>((resolve) => http.listen(0, '127.0.0.1', resolve));
      const port = (http.address() as AddressInfo).port;

      for (const auth of [{}, { token: signHandshakeTicket(SECRET, host, TENANT_ID, grant(true)) }]) {
        const client = connectClient(`http://127.0.0.1:${port}${namespace}`, {
          transports: ['websocket'],
          reconnection: false,
          extraHeaders: { 'X-Forwarded-Host': host },
          auth,
        });
        clients.push(client);

        const error = await new Promise<Error>((resolve) => client.once('connect_error', resolve));

        expect(error.message).toBe(SHARE_REALTIME_UNSUPPORTED);
      }
      expect(isCurrentTenant).not.toHaveBeenCalled();
      expect(holdsTenantNow).not.toHaveBeenCalled();
      expect(onApexUnresolved).not.toHaveBeenCalled();
      expect(onTenantAdmitted).not.toHaveBeenCalled();
      expect(fetchMock).not.toHaveBeenCalled();
      await server.stop();
    },
  );
});
