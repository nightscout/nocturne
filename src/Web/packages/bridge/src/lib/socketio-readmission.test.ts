import { describe, it, expect, vi, afterEach } from 'vitest';
import { createServer } from 'http';
import type { AddressInfo } from 'node:net';
import { io as connectClient, type Socket as ClientSocket } from 'socket.io-client';
import SocketIOServer, {
  ADMISSION_RECHECK_INTERVAL_MS,
  UNANSWERED_COLLECTION_RETRY_MS,
} from './socketio-server.js';
import { signHandshakeTicket, type RealtimeGrant } from './handshake-ticket.js';

const SECRET = 'test-instance-key-0123456789';
const HOST = 'rhys.nocturne.run';

function grant(extra: Partial<RealtimeGrant> = {}): RealtimeGrant {
  return { tenantRelay: true, categories: [], historyClamped: false, mayAck: false, ...extra };
}

function admission(body: unknown) {
  return { ok: true, status: 200, json: () => Promise.resolve(body) };
}

const refused = { ok: false, status: 401 };
const failing = { ok: false, status: 503 };

function makeServer() {
  return new SocketIOServer(createServer(), {}, 'nocturne.run', null, SECRET, 'http://api.internal');
}

/** A socket already past the handshake, with the members a re-check touches. */
function admittedSocket() {
  return {
    id: 'sock1',
    connected: true,
    data: { pendingTenantSlug: 'rhys' } as Record<string, unknown>,
    join: vi.fn(),
    leave: vi.fn(),
    disconnect: vi.fn(),
  };
}

describe('SocketIOServer re-admission of a socket admitted on its own credential', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('re-checks the credential on an interval and disconnects once the API refuses it', async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn().mockResolvedValue(admission({ tenantRelay: true }));
    vi.stubGlobal('fetch', fetchMock);
    const server = makeServer();
    const socket = admittedSocket();
    await server.handleAuthorize(socket as never, { secret: 'sha1hash' });

    fetchMock.mockResolvedValue(refused);
    await vi.advanceTimersByTimeAsync(ADMISSION_RECHECK_INTERVAL_MS);

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[1][1].headers['api-secret']).toBe('sha1hash');
    expect(socket.disconnect).toHaveBeenCalledWith(true);
  });

  it('leaves the rooms a narrowed admission no longer grants', async () => {
    const subject = '0a5f2c1e-1111-4222-8333-444455556666';
    const fetchMock = vi.fn().mockResolvedValue(
      admission({ tenantRelay: true, subjectId: subject, categories: ['entries', 'care'], historyClamped: false }),
    );
    vi.stubGlobal('fetch', fetchMock);
    const server = makeServer();
    const socket = admittedSocket();
    await server.handleAuthorize(socket as never, { token: 'phone-token' });

    fetchMock.mockResolvedValue(
      admission({ tenantRelay: true, subjectId: subject, categories: ['entries'], historyClamped: false }),
    );
    await server.readmit(socket as never);

    expect(socket.leave.mock.calls.map(([room]) => room)).toEqual(['tenant:rhys:category:care']);
    expect(socket.disconnect).not.toHaveBeenCalled();
  });

  it('leaves every room when the admission turns restricted', async () => {
    const fetchMock = vi.fn().mockResolvedValue(admission({ tenantRelay: true, categories: ['entries'], historyClamped: false }));
    vi.stubGlobal('fetch', fetchMock);
    const server = makeServer();
    const socket = admittedSocket();
    await server.handleAuthorize(socket as never, { token: 'phone-token' });

    fetchMock.mockResolvedValue(admission({ tenantRelay: false }));
    await server.readmit(socket as never);

    expect(socket.leave.mock.calls.map(([room]) => room).sort()).toEqual(
      ['tenant:rhys', 'tenant:rhys:category:entries'].sort(),
    );
  });

  it('keeps the socket as it is while the API gives no answer', async () => {
    const fetchMock = vi.fn().mockResolvedValue(admission({ tenantRelay: true }));
    vi.stubGlobal('fetch', fetchMock);
    const server = makeServer();
    const socket = admittedSocket();
    await server.handleAuthorize(socket as never, { secret: 'sha1hash' });

    fetchMock.mockResolvedValue(failing);
    await server.readmit(socket as never);
    fetchMock.mockRejectedValue(new Error('ECONNREFUSED'));
    await server.readmit(socket as never);

    expect(socket.disconnect).not.toHaveBeenCalled();
    expect(socket.leave).not.toHaveBeenCalled();
  });

  it('drops a /storage collection the token can no longer read, and disconnects once none is left', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200 });
    vi.stubGlobal('fetch', fetchMock);
    const server = makeServer();
    const socket = admittedSocket();
    await server.handleStorageSubscribe(socket as never, { accessToken: 'aaps', collections: ['entries', 'treatments'] });

    fetchMock.mockImplementation((url: URL) =>
      Promise.resolve(String(url).includes('/treatments') ? refused : { ok: true, status: 200 }));
    await server.readmit(socket as never);

    expect(socket.leave.mock.calls.map(([room]) => room)).toEqual(['storage:rhys:treatments']);
    expect(socket.disconnect).not.toHaveBeenCalled();

    fetchMock.mockResolvedValue(refused);
    await server.readmit(socket as never);

    expect(socket.leave.mock.calls.map(([room]) => room)).toEqual(['storage:rhys:treatments', 'storage:rhys:entries']);
    expect(socket.disconnect).toHaveBeenCalledWith(true);
  });

  it('joins a /storage collection whose probe went unanswered once the API answers', async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn().mockImplementation((url: URL) =>
      String(url).includes('/treatments')
        ? Promise.reject(new DOMException('The operation was aborted due to timeout', 'TimeoutError'))
        : Promise.resolve({ ok: true, status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    const server = makeServer();
    const socket = admittedSocket();
    const ack = vi.fn();
    await server.handleStorageSubscribe(socket as never, { accessToken: 'aaps', collections: ['entries', 'treatments'] }, ack);

    expect(ack).toHaveBeenCalledWith({ success: true, collections: ['entries'] });
    expect(socket.join.mock.calls.map(([room]) => room)).toEqual(['storage:rhys:entries']);

    fetchMock.mockResolvedValue({ ok: true, status: 200 });
    await vi.advanceTimersByTimeAsync(UNANSWERED_COLLECTION_RETRY_MS);

    expect(socket.join.mock.calls.map(([room]) => room)).toEqual(['storage:rhys:entries', 'storage:rhys:treatments']);
    expect(socket.leave).not.toHaveBeenCalled();
    expect(socket.disconnect).not.toHaveBeenCalled();
  });

  it('keeps a /storage socket the API could not answer for at all, and joins it once the API answers', async () => {
    const fetchMock = vi.fn().mockResolvedValue(failing);
    vi.stubGlobal('fetch', fetchMock);
    const server = makeServer();
    const socket = admittedSocket();
    const ack = vi.fn();
    await server.handleStorageSubscribe(socket as never, { accessToken: 'aaps', collections: ['entries', 'treatments'] }, ack);

    expect(ack).toHaveBeenCalledWith({ success: true, collections: [] });
    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.disconnect).not.toHaveBeenCalled();

    fetchMock.mockRejectedValue(new Error('ECONNREFUSED'));
    await server.readmit(socket as never);
    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.disconnect).not.toHaveBeenCalled();

    fetchMock.mockResolvedValue({ ok: true, status: 200 });
    await server.readmit(socket as never);
    expect(socket.join.mock.calls.map(([room]) => room)).toEqual(['storage:rhys:entries', 'storage:rhys:treatments']);
  });

  it('disconnects a /storage socket once the API refuses every collection it could not answer for', async () => {
    const fetchMock = vi.fn().mockResolvedValue(failing);
    vi.stubGlobal('fetch', fetchMock);
    const server = makeServer();
    const socket = admittedSocket();
    await server.handleStorageSubscribe(socket as never, { accessToken: 'aaps', collections: ['entries'] });

    fetchMock.mockResolvedValue(refused);
    await server.readmit(socket as never);

    expect(socket.join).not.toHaveBeenCalled();
    expect(socket.disconnect).toHaveBeenCalledWith(true);
  });

  it.each([
    ['refuses the token', refused],
    ['no longer admits it to the tenant relay', admission({ tenantRelay: false })],
  ])('disconnects an /alarm socket once the API %s', async (_case, answer) => {
    const fetchMock = vi.fn().mockResolvedValue(admission({ tenantRelay: true }));
    vi.stubGlobal('fetch', fetchMock);
    const server = makeServer();
    const socket = admittedSocket();
    await server.handleAlarmSubscribe(socket as never, { accessToken: 'aaps' });

    fetchMock.mockResolvedValue(failing);
    await server.readmit(socket as never);
    expect(socket.disconnect).not.toHaveBeenCalled();

    fetchMock.mockResolvedValue(answer);
    await server.readmit(socket as never);
    expect(socket.disconnect).toHaveBeenCalledWith(true);
  });

  it('follows the right to silence alarms across re-checks of an /alarm socket', async () => {
    const fetchMock = vi.fn().mockResolvedValue(admission({ tenantRelay: true, mayAck: false }));
    vi.stubGlobal('fetch', fetchMock);
    const server = makeServer();
    const onAlarmAck = vi.fn();
    server.onAlarmAck = onAlarmAck;
    const socket = admittedSocket();
    await server.handleAlarmSubscribe(socket as never, { accessToken: 'aaps' });

    fetchMock.mockResolvedValue(admission({ tenantRelay: true, mayAck: true }));
    await server.readmit(socket as never);
    server.handleAlarmAck(socket as never, 2, 'granted', 60000);

    fetchMock.mockResolvedValue(admission({ tenantRelay: true, mayAck: false }));
    await server.readmit(socket as never);
    server.handleAlarmAck(socket as never, 2, 'revoked', 60000);

    expect(onAlarmAck).toHaveBeenCalledExactlyOnceWith('rhys', 2, 'granted', 60000);
  });
});

describe('SocketIOServer re-admission of a browser socket admitted on a ticket', () => {
  const clients: ClientSocket[] = [];

  afterEach(() => {
    for (const client of clients.splice(0)) client.disconnect();
  });

  async function listening() {
    const http = createServer();
    const server = new SocketIOServer(http, {}, 'nocturne.run', null, SECRET);
    await server.start();
    await new Promise<void>((resolve) => http.listen(0, '127.0.0.1', resolve));
    return { server, port: (http.address() as AddressInfo).port };
  }

  type TicketReply = { token: string | null; denied: boolean };

  /** A browser admitted on `admitted` that answers each request for a fresh ticket with `answer()`. */
  async function browser(port: number, admitted: RealtimeGrant, answer: () => TicketReply) {
    const client = connectClient(`http://127.0.0.1:${port}`, {
      transports: ['websocket'],
      reconnection: false,
      extraHeaders: { 'X-Forwarded-Host': HOST },
      auth: { token: signHandshakeTicket(SECRET, HOST, admitted) },
    });
    clients.push(client);
    client.on('realtime_ticket', (ack: (reply: TicketReply) => void) => ack(answer()));
    await new Promise<void>((resolve) => client.once('connect_ack', () => resolve()));
    return client;
  }

  function serverSocket(server: SocketIOServer, client: ClientSocket) {
    const socket = server.getIO()!.of('/').sockets.get(client.id!);
    expect(socket).toBeDefined();
    return socket!;
  }

  it('moves the socket onto the rooms of the fresh ticket', async () => {
    const { server, port } = await listening();
    const client = await browser(port, grant({ categories: ['entries', 'care'] }), () => ({
      token: signHandshakeTicket(SECRET, HOST, grant({ categories: ['entries'] })),
      denied: false,
    }));
    const socket = serverSocket(server, client);

    await server.readmit(socket);

    expect([...socket.rooms].filter((room) => room !== socket.id).sort()).toEqual(
      ['tenant:rhys', 'tenant:rhys:category:entries'].sort(),
    );
    await server.stop();
  });

  it.each([
    ['a denial', () => ({ token: null, denied: true })],
    ['a ticket minted for another host', () => ({ token: signHandshakeTicket(SECRET, 'other.nocturne.run', grant()), denied: false })],
    ['a tampered ticket', () => ({ token: signHandshakeTicket('another-key', HOST, grant()), denied: false })],
  ])('closes the transport of a socket that answers with %s', async (_case, answer) => {
    const { server, port } = await listening();
    const client = await browser(port, grant({ categories: ['entries'] }), answer);
    const socket = serverSocket(server, client);
    const disconnected = new Promise<string>((resolve) => client.once('disconnect', resolve));

    await server.readmit(socket);

    expect(await disconnected).toBe('transport close');
    expect(socket.connected).toBe(false);
    await server.stop();
  });

  it('keeps a socket whose browser could not fetch a ticket, until one more ticket lifetime has passed', async () => {
    const { server, port } = await listening();
    const client = await browser(port, grant({ categories: ['entries'] }), () => ({ token: null, denied: false }));
    const socket = serverSocket(server, client);
    const rooms = [...socket.rooms].sort();

    await server.readmit(socket);

    expect(socket.connected).toBe(true);
    expect([...socket.rooms].sort()).toEqual(rooms);

    const disconnected = new Promise<string>((resolve) => client.once('disconnect', resolve));
    socket.data.ticketGraceUntil = Date.now() - 1;
    await server.readmit(socket);

    expect(await disconnected).toBe('transport close');
    await server.stop();
  });
});
