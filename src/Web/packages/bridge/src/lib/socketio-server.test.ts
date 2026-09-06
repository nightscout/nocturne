import { describe, it, expect, vi, afterEach } from 'vitest';
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
    const ticket = signHandshakeTicket(SECRET, 'rhys.nocturne.run');
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
      { token: signHandshakeTicket(SECRET, 'someone-else.nocturne.run') },
    );

    await server.authorizeHandshake(socket as never, next);

    expect(next.mock.calls[0][0].message).toBe('unauthorized');
    expect(socket.data.tenantSlug).toBeUndefined();
  });

  it('rejects an expired ticket so the client refetches instead of going quiet', async () => {
    const server = makeServer();
    const next = vi.fn();
    // Sign a ticket that expired one minute ago.
    const expired = signHandshakeTicket(SECRET, 'rhys.nocturne.run', -60_000);
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
    return new SocketIOServer(
      createServer(),
      {},
      'nocturne.run',
      [],
      SECRET,
      apiBaseUrl,
    );
  }

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('joins the tenant room when the API accepts the credential', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200 });
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
    expect(String(url)).toContain('/api/v1/entries');
    expect(init.headers['api-secret']).toBe('sha1hash');
    expect(init.headers['X-Forwarded-Host']).toBe('rhys.nocturne.run');
    expect(init.headers['X-Instance-Key']).toBeUndefined();
  });

  it('passes a subject token through as a query parameter', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200 });
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

  it('refuses to authorize when no credentials are supplied', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    const server = makeApiServer();
    const socket = pendingSocket('rhys');

    await server.handleAuthorize(socket as never, {});

    expect(fetchMock).not.toHaveBeenCalled();
    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.disconnect).toHaveBeenCalled();
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

// ---------------------------------------------------------------------------
// v3 /storage namespace — AAPS / xDrip+ / NightGuard uploaders
// ---------------------------------------------------------------------------

describe('SocketIOServer v3 /storage namespace', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function makeStorageServer(apiBaseUrl = 'http://api.internal') {
    return new SocketIOServer(createServer(), {}, 'nocturne.run', [], SECRET, apiBaseUrl);
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
      collections: ['devicestatus', 'entries', 'profile', 'treatments', 'foods', 'settings'],
    });
    expect(socket.join).toHaveBeenCalledTimes(6);
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
    expect(String(url)).toContain('count=1');
    expect(init.headers['Authorization']).toBe('Bearer aaps-token');
    expect(init.headers['X-Forwarded-Host']).toBe('rhys.nocturne.run');
    // The bridge must NOT use its instance key to authorize the client.
    expect(init.headers['X-Instance-Key']).toBeUndefined();
    expect(init.headers['X-Instance-Service']).toBeUndefined();
  });
});

describe('SocketIOServer v3 /storage broadcast fan-out', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  /** Build a started server with a real socket.io Namespace so we can assert
   *  on the actual emit target and payload via the BroadcastingWildcard. */
  async function startedServer() {
    const server = new SocketIOServer(
      createServer(),
      {},
      'nocturne.run',
      [],
      SECRET,
      'http://api.internal',
    );
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

  it('amends the payload colName for food -> foods', async () => {
    const server = await startedServer();
    const storageNsp = server.getIO()!.of('/storage');

    const emitted: { event: string; payload: unknown }[] = [];
    vi.spyOn(storageNsp, 'to').mockReturnValue({
      emit: (event: string, payload: unknown) => emitted.push({ event, payload }),
    } as never);

    server.broadcastStorageEvent('create', { colName: 'food', doc: {} }, 'rhys');

    expect((emitted[0].payload as { colName: string }).colName).toBe('foods');
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
    return new SocketIOServer(createServer(), {}, 'nocturne.run', [], SECRET, apiBaseUrl);
  }

  function alarmSocket(pendingTenantSlug?: string) {
    return {
      id: 'alarm-1',
      data: { pendingTenantSlug } as Record<string, unknown>,
      join: vi.fn(),
      disconnect: vi.fn(),
    };
  }

  it('subscribes for alarms when the token is valid', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, status: 200 }));
    const server = makeAlarmServer();
    const socket = alarmSocket('rhys');
    const ack = vi.fn();

    await server.handleAlarmSubscribe(socket as never, { accessToken: 'tok' }, ack);

    expect(ack).toHaveBeenCalledWith({ success: true, message: 'Subscribed for alarms' });
    expect(socket.join).toHaveBeenCalledWith('alarm:rhys');
    expect(socket.data.tenantSlug).toBe('rhys');
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
});

describe('SocketIOServer v3 /alarm broadcast fan-out', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  async function startedServer() {
    const server = new SocketIOServer(
      createServer(),
      {},
      'nocturne.run',
      [],
      SECRET,
      'http://api.internal',
    );
    await server.start();
    return server;
  }

  it('fans an alarm out to the /alarm tenant room', async () => {
    const server = await startedServer();
    const alarmNsp = server.getIO()!.of('/alarm');
    const toSpy = vi.spyOn(alarmNsp, 'to');

    server.broadcastAlarm({ level: 'urgent', message: 'HIGH' }, 'rhys');

    expect(toSpy).toHaveBeenCalledWith('alarm:rhys');
    server.getIO()!.close();
  });

  it('fans a clear_alarm out to the /alarm tenant room', async () => {
    const server = await startedServer();
    const alarmNsp = server.getIO()!.of('/alarm');
    const toSpy = vi.spyOn(alarmNsp, 'to');

    server.broadcastClearAlarm('rhys');

    expect(toSpy).toHaveBeenCalledWith('alarm:rhys');
    server.getIO()!.close();
  });

  it('fans an announcement out to the /alarm tenant room', async () => {
    const server = await startedServer();
    const alarmNsp = server.getIO()!.of('/alarm');
    const toSpy = vi.spyOn(alarmNsp, 'to');

    server.broadcastAnnouncement({ message: 'sensor change' }, 'rhys');

    expect(toSpy).toHaveBeenCalledWith('alarm:rhys');
    server.getIO()!.close();
  });
});
