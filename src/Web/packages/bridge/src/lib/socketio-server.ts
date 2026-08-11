import { Server as SocketIOServerClass, Socket, Namespace } from 'socket.io';
import { Server as HttpServer } from 'http';
import logger from './logger.js';
import type { ClientInfo, AlarmData, ServerStats } from '../types.js';
import { verifyHandshakeTicket, normalizeHandshakeHost } from './handshake-ticket.js';

/**
 * The six collection names the Nightscout v3 `/storage` namespace lets a client
 * subscribe to (AndroidAPS sends exactly these, lowercase). Not all of them are
 * broadcast by the API today — `settings` has no realtime path — but the client
 * is allowed to ask for any of them.
 */
const KNOWN_STORAGE_COLLECTIONS = [
  'devicestatus',
  'entries',
  'profile',
  'treatments',
  'foods',
  'settings',
] as const;

/**
 * The API broadcasts storage events under its own collection names, which don't
 * all match the names v3 clients subscribe with: the API uses the singular
 * `food` and the plural `profiles`, while AAPS subscribes to `foods` and
 * `profile`. To route a broadcast to the sockets that asked for it we have to
 * translate the broadcast's collection name into the name those sockets joined
 * their room under. `clientCollectionName` returns the v3-client spelling for a
 * given broadcast collection; anything not in the map is returned unchanged.
 */
const BROADCAST_TO_CLIENT_COLLECTION: Record<string, string> = {
  food: 'foods',
  foods: 'foods',
  profiles: 'profile',
  profile: 'profile',
};

/** Translate an API broadcast collection name to the v3-client spelling. */
function clientCollectionName(broadcastCollection: string): string {
  return BROADCAST_TO_CLIENT_COLLECTION[broadcastCollection] ?? broadcastCollection;
}

/** Room a `/storage` socket joins for a tenant + collection (client spelling). */
function storageRoom(tenantSlug: string, clientCollection: string): string {
  return `storage:${tenantSlug}:${clientCollection}`;
}

/** Room a `/alarm` socket joins for a tenant. */
function alarmRoom(tenantSlug: string): string {
  return `alarm:${tenantSlug}`;
}

/**
 * Map each v3-client collection name to the API v3 endpoint path it reads from.
 * The API's controller names don't always match the client collection names
 * (`foods` → `/api/v3/food`, singular), so this is used to probe per-collection
 * read authorization: a token that can read `/api/v3/entries` is authorized for
 * the `entries` room, but not necessarily for `treatments` or `settings`.
 */
const COLLECTION_READ_ENDPOINT: Record<string, string> = {
  entries: '/api/v3/entries',
  treatments: '/api/v3/treatments',
  devicestatus: '/api/v3/devicestatus',
  profile: '/api/v3/profile',
  foods: '/api/v3/food',
  settings: '/api/v3/settings',
};

interface SocketIOConfig {
  cors?: {
    origin: string | string[];
    methods?: string[];
    credentials?: boolean;
  };
  transports?: ('websocket' | 'polling')[];
  pingTimeout?: number;
  pingInterval?: number;
}

/** Pick the host the connection arrived on: the proxy-forwarded host if present
 *  (X-Forwarded-Host), otherwise the Host header. Returns the first value when a
 *  header is repeated.
 *
 *  Trust model: X-Forwarded-Host is NOT sanitized at the edge — the YARP gateway
 *  forwards it as-is and nothing overwrites it, so a client can set it to any
 *  value. Safety does not depend on trusting this header. The chosen host is
 *  replayed to the API authorization probe together with the client's OWN cookie,
 *  and the API applies its per-tenant read policy: a spoofed host only re-points
 *  the probe at another tenant, and a private tenant still rejects a non-member
 *  cookie, so spoofing can expose only data that is already public. The API
 *  resolves tenants from the same header in TenantResolutionMiddleware, so the
 *  bridge adds no new trust assumption. */
export function pickHandshakeHost(
  headers: Record<string, string | string[] | undefined>,
): string | undefined {
  const value = headers['x-forwarded-host'] ?? headers['host'];
  const single = Array.isArray(value) ? value[0] : value;
  return single || undefined;
}

/** Resolve the tenant slug a host belongs to. A subdomain resolves to its slug;
 *  the apex domain resolves to the sole tenant when exactly one exists, mirroring
 *  the API's tenant resolution so a single tenant served on the root domain works
 *  without a subdomain. Returns null when the host is foreign or the apex can't be
 *  resolved to a single tenant. */
/** Socket.IO query values are `string | string[]`; take the first. */
function queryValue(value: string | string[] | undefined): string | undefined {
  const single = Array.isArray(value) ? value[0] : value;
  return single || undefined;
}

export function resolveTenantSlug(
  host: string | undefined,
  baseDomain: string,
  tenantSlugs: string[],
): string | null {
  if (!host) return null;

  const hostname = host.split(':')[0];
  const baseDomainHost = baseDomain.split(':')[0];

  if (hostname.endsWith(`.${baseDomainHost}`)) {
    const slug = hostname.slice(0, -(baseDomainHost.length + 1));
    return slug || null;
  }

  if (hostname === baseDomainHost && tenantSlugs.length === 1) {
    return tenantSlugs[0];
  }

  return null;
}

class SocketIOServer {
  private io: SocketIOServerClass | null = null;
  private httpServer: HttpServer;
  private clients: Map<string, ClientInfo> = new Map();
  private config: SocketIOConfig;
  private baseDomain: string;
  private tenantSlugs: string[];
  private signingSecret: string;
  private apiBaseUrl: string;
  /** Nightscout v3 namespaces for uploaders (AAPS, xDrip+, ...). null until
   *  start() attaches them to the same httpServer as the default namespace. */
  private storageNsp: Namespace | null = null;
  private alarmNsp: Namespace | null = null;

  constructor(
    httpServer: HttpServer,
    config: SocketIOConfig = {},
    baseDomain: string,
    tenantSlugs: string[] = [],
    signingSecret: string = '',
    apiBaseUrl: string = '',
  ) {
    this.httpServer = httpServer;
    this.baseDomain = baseDomain;
    this.tenantSlugs = tenantSlugs;
    this.signingSecret = signingSecret;
    this.apiBaseUrl = apiBaseUrl;
    this.config = {
      cors: config.cors ?? { origin: '*', methods: ['GET', 'POST'], credentials: true },
      transports: config.transports || ['websocket', 'polling'],
      pingTimeout: config.pingTimeout || 60000,
      pingInterval: config.pingInterval || 25000
    };
  }

  start(): Promise<void> {
    return new Promise((resolve, reject) => {
      try {
        // Create Socket.IO server attached to existing HTTP server
        this.io = new SocketIOServerClass(this.httpServer, {
          cors: this.config.cors,
          transports: this.config.transports as any,
          pingTimeout: this.config.pingTimeout,
          pingInterval: this.config.pingInterval,
          // Legacy Nightscout clients (LoopFollow's socket.io-client-swift) speak
          // Engine.IO v3 and are otherwise rejected with "Unsupported protocol
          // version" before any handler runs.
          allowEIO3: true
        });

        this.setupHandshakeAuth();
        this.setupEventHandlers();
        this.setupStorageNamespace();
        this.setupAlarmNamespace();

        logger.info('Socket.IO server attached to HTTP server');
        resolve();

      } catch (error) {
        logger.error('Failed to start Socket.IO server:', error);
        reject(error);
      }
    });
  }

  /** Authorize every handshake before it can join a tenant room. The tenant is
   *  resolved from the connection's Host, and the connection must present a valid
   *  handshake ticket (see handshake-ticket.ts) in its Socket.IO `auth` payload.
   *  The ticket is minted by the web app's `/realtime/ticket` endpoint only after
   *  it has replayed the connection's read against the API's per-tenant read
   *  policy, so verifying the ticket here mirrors that policy without a
   *  per-connection API call. Unauthorized handshakes are rejected so the socket
   *  never receives broadcasts. */
  private setupHandshakeAuth(): void {
    if (!this.io) return;
    this.io.use((socket, next) => this.authorizeHandshake(socket, next));
  }

  /** Resolve the tenant for a handshake and authorize it from its ticket. Sets
   *  `socket.data.tenantSlug` for room assignment on success; calls `next` with
   *  an error to reject. Exposed for unit testing. */
  async authorizeHandshake(socket: Socket, next: (err?: Error) => void): Promise<void> {
    try {
      const host = pickHandshakeHost(socket.handshake.headers);
      const tenantSlug = resolveTenantSlug(host, this.baseDomain, this.tenantSlugs);
      if (!tenantSlug) {
        logger.warn(`Rejecting connection ${socket.id}: no resolvable tenant`);
        return next(new Error('tenant_unresolved'));
      }

      // Engine.IO v3 clients have no `auth` payload — that arrived with the v4
      // protocol — so also accept the ticket from the handshake query.
      const token =
        (socket.handshake.auth as { token?: string } | undefined)?.token
        ?? queryValue(socket.handshake.query?.token);
      if (token) {
        // A ticket was offered, so this is the browser path: reject it outright if
        // it doesn't verify. Admitting it silently would leave a client whose
        // ticket merely expired connected but receiving nothing, instead of
        // getting connect_error and retrying with a fresh one.
        const ticket = verifyHandshakeTicket(this.signingSecret, token);
        if (!ticket) {
          logger.warn(`Rejecting connection ${socket.id}: invalid handshake ticket for tenant ${tenantSlug}`);
          return next(new Error('unauthorized'));
        }

        // Bind the ticket to the host the connection actually arrived on, so a
        // ticket minted for one tenant can't be replayed on another tenant's socket.
        if (ticket.h !== normalizeHandshakeHost(host!)) {
          logger.warn(`Rejecting connection ${socket.id}: handshake ticket host mismatch for tenant ${tenantSlug}`);
          return next(new Error('unauthorized'));
        }

        socket.data.tenantSlug = tenantSlug;
        return next();
      }

      // No ticket offered at all: admit the socket unauthorized so a legacy
      // Nightscout client can complete the classic `authorize` exchange, which it
      // sends only after connecting. The socket joins no room until that succeeds,
      // so it receives nothing in the meantime.
      logger.debug(`Connection ${socket.id} carries no ticket; awaiting classic authorize for tenant ${tenantSlug}`);
      socket.data.pendingTenantSlug = tenantSlug;
      next();
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      logger.error(`Rejecting connection ${socket.id}: authorization error: ${message}`);
      next(new Error('authorization_error'));
    }
  }

  private setupEventHandlers(): void {
    if (!this.io) return;

    this.io.on('connection', (socket: Socket) => {
      const clientId = socket.id;
      const clientInfo: ClientInfo = {
        id: clientId,
        connectedAt: new Date(),
        address: socket.handshake.address,
        userAgent: socket.handshake.headers['user-agent']
      };

      this.clients.set(clientId, clientInfo);
      logger.info(`Client connected: ${clientId} from ${clientInfo.address}`);
      logger.debug(`Total connected clients: ${this.clients.size}`);

      // Join the client to the tenant room resolved and authorized during the
      // handshake (see setupHandshakeAuth).
      const tenantSlug = socket.data.tenantSlug as string | undefined;
      if (tenantSlug) {
        socket.join(`tenant:${tenantSlug}`);
        logger.info(`Client ${clientId} joined tenant room: ${tenantSlug}`);
      }

      // Classic Nightscout authorization: legacy clients connect first and then
      // send their credentials in an `authorize` message.
      socket.on('authorize', (payload: unknown, callback?: (result: unknown) => void) => {
        void this.handleAuthorize(socket, payload, callback);
      });

      // Handle client disconnection
      socket.on('disconnect', (reason: string) => {
        this.clients.delete(clientId);
        logger.info(`Client disconnected: ${clientId}, reason: ${reason}`);
        logger.debug(`Total connected clients: ${this.clients.size}`);
      });

      // Send initial connection acknowledgment
      socket.emit('connect_ack', {
        clientId: clientId,
        serverTime: new Date().toISOString(),
        version: '1.0.0'
      });
    });
  }

  /** Handle the classic Nightscout `authorize` message.
   *
   *  Legacy clients (LoopFollow, Nightscout watchfaces) don't have — and can't
   *  obtain — a handshake ticket, because /realtime/ticket mints one only after
   *  replaying the read against the API with the caller's browser session. They
   *  authenticate the way they do against classic Nightscout instead: an API
   *  secret (already SHA-1 hashed by the client) and/or a subject token.
   *
   *  Rather than interpret those credentials here, replay them against the same
   *  read the ticket endpoint probes. The API applies its own per-tenant policy,
   *  so this grants exactly what a REST read with the same credential would.
   *  Only on success does the socket join its tenant room.
   *
   *  The probe carries the client's credential and nothing else — never the
   *  bridge's instance key, which would authenticate any anonymous caller as a
   *  service and hand them another tenant's data. */
  async handleAuthorize(
    socket: Socket,
    payload: unknown,
    callback?: (result: unknown) => void,
  ): Promise<void> {
    const deny = (reason: string) => {
      logger.warn(`Authorize failed for ${socket.id}: ${reason}`);
      callback?.({ read: false, write: false, write_treatment: false });
      socket.disconnect(true);
    };

    // Already authorized by a valid handshake ticket — nothing more to do.
    if (socket.data.tenantSlug) {
      callback?.({ read: true, write: false, write_treatment: false });
      return;
    }

    const tenantSlug = socket.data.pendingTenantSlug as string | undefined;
    if (!tenantSlug) return deny('no resolvable tenant');

    if (!this.apiBaseUrl) return deny('bridge has no API base URL configured');

    const message = (payload ?? {}) as { secret?: unknown; token?: unknown };
    const secret = typeof message.secret === 'string' ? message.secret : undefined;
    const token = typeof message.token === 'string' ? message.token : undefined;
    if (!secret && !token) return deny('no credentials supplied');

    try {
      const url = new URL(`${this.apiBaseUrl}/api/v1/entries`);
      url.searchParams.set('count', '1');
      if (token) url.searchParams.set('token', token);

      const headers: Record<string, string> = {
        'X-Forwarded-Host': `${tenantSlug}.${this.baseDomain}`,
        // The API's response cache runs before auth and keys on the internal
        // Host, so a cached authenticated 200 could otherwise authorize an
        // unauthenticated probe.
        'Cache-Control': 'no-cache, no-store',
      };
      if (secret) headers['api-secret'] = secret;

      const probe = await fetch(url, {
        method: 'GET',
        headers,
        signal: AbortSignal.timeout(5000),
      });

      if (!probe.ok) return deny(`API denied the credential (${probe.status})`);

      socket.data.tenantSlug = tenantSlug;
      socket.data.pendingTenantSlug = undefined;
      socket.join(`tenant:${tenantSlug}`);
      logger.info(`Client ${socket.id} authorized via legacy credentials for tenant: ${tenantSlug}`);
      callback?.({ read: true, write: false, write_treatment: false });
    } catch (error) {
      const reason = error instanceof Error ? error.message : String(error);
      return deny(`credential probe failed: ${reason}`);
    }
  }

  /** Return the Socket.IO emit target for a tenant room.
   *
   *  Always scoped: an unscoped broadcast would also reach sockets that are
   *  connected but not yet authorized (legacy clients mid-`authorize`), which
   *  would leak one tenant's data to them. */
  private emitTarget(tenantSlug?: string) {
    if (!this.io) return null;
    if (!tenantSlug) {
      logger.warn('Refusing to broadcast without a tenant slug');
      return null;
    }
    return this.io.to(`tenant:${tenantSlug}`);
  }

  // Methods to broadcast messages to clients
  broadcastDataUpdate(data: any, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    logger.debug(`Broadcasting dataUpdate${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('dataUpdate', data);
  }

  broadcastAnnouncement(message: any, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    logger.debug(`Broadcasting announcement${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('announcement', message);

    if (this.alarmNsp && tenantSlug) {
      this.alarmNsp.to(alarmRoom(tenantSlug)).emit('announcement', message);
    }
  }

  broadcastAlarm(alarm: AlarmData, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    const eventName = alarm.level === 'urgent' ? 'urgent_alarm' : 'alarm';
    logger.debug(`Broadcasting ${eventName}${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit(eventName, alarm);

    if (this.alarmNsp && tenantSlug) {
      this.alarmNsp.to(alarmRoom(tenantSlug)).emit(eventName, alarm);
    }
  }

  broadcastClearAlarm(tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    logger.debug(`Broadcasting clear_alarm${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('clear_alarm');

    if (this.alarmNsp && tenantSlug) {
      this.alarmNsp.to(alarmRoom(tenantSlug)).emit('clear_alarm');
    }
  }

  broadcastNotification(notification: any, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    logger.debug(`Broadcasting notification${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('notification', notification);
  }

  broadcastStatusUpdate(status: any, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    logger.debug(`Broadcasting status update${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('status', status);
  }

  broadcastStorageEvent(eventType: 'create' | 'update' | 'delete', data: any, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    const clientCount = this.clients.size;
    logger.debug(`Broadcasting storage ${eventType} event to ${clientCount} connected clients${tenantSlug ? ` (tenant: ${tenantSlug})` : ''}`);

    if (clientCount === 0) {
      logger.warn('No Socket.IO clients connected - events will not be delivered to frontend');
    }

    target.emit(eventType, data);

    // Fan out to v3 `/storage` namespace clients (AAPS, xDrip+, ...). The event
    // is delivered only to sockets subscribed to the matching collection room,
    // translating the API's collection spelling (e.g. `profiles`, `food`) to the
    // v3-client spelling (`profile`, `foods`) so a socket that subscribed to
    // `profile` receives broadcasts the API emits as `profiles`. The payload's
    // `colName` is amended to the client spelling too, since AAPS routes the
    // event by reading `colName` from the payload.
    if (this.storageNsp && tenantSlug) {
      const broadcastCollection =
        typeof data?.colName === 'string' ? data.colName
        : typeof data?.collection === 'string' ? data.collection
        : null;
      if (broadcastCollection) {
        const clientCollection = clientCollectionName(broadcastCollection);
        this.storageNsp
          .to(storageRoom(tenantSlug, clientCollection))
          .emit(eventType, { ...data, colName: clientCollection });
      }
    }
  }

  broadcastInAppNotification(eventType: 'notificationCreated' | 'notificationArchived' | 'notificationUpdated', data: any, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    logger.debug(`Broadcasting ${eventType}${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit(eventType, data);
  }

  broadcastSyncProgress(data: any, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;
    logger.debug(`Broadcasting syncProgress${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('syncProgress', data);
  }

  broadcastConfigChanged(data: any, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;
    logger.debug(`Broadcasting configChanged${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('configChanged', data);
  }

  // Send message to specific room
  sendToRoom(room: string, event: string, data: any): void {
    if (!this.io) return;

    logger.debug(`Sending ${event} to room: ${room}`);
    this.io.to(room).emit(event, data);
  }

  // Get server statistics
  getStats(): ServerStats {
    return {
      connectedClients: this.clients.size,
      clients: Array.from(this.clients.values()),
      uptime: process.uptime()
    };
  }

  setTenantSlugs(slugs: string[]): void {
    this.tenantSlugs = slugs;
  }

  getIO(): SocketIOServerClass | null {
    return this.io;
  }

  /**
   * Resolve the tenant for a v3-namespace connection (AAPS, xDrip+, ...) from
   * the handshake Host. Unlike browser connections on the default namespace,
   * these clients carry no signed handshake ticket — they authenticate in-band
   * via the `subscribe` event — so the handshake only resolves the tenant and
   * admits the socket unauthorized, exactly like the legacy `authorize` path.
   * Returns the slug, or null when the host resolves to no tenant. Exposed for
   * unit testing.
   */
  resolveNamespaceTenant(socket: Socket): string | null {
    const host = pickHandshakeHost(socket.handshake.headers);
    return resolveTenantSlug(host, this.baseDomain, this.tenantSlugs);
  }

  /**
   * Replay an AAPS access token against the API to authorize a v3-namespace
   * `subscribe`. This mirrors the legacy `handleAuthorize` probe (#547): the
   * token is sent to the API scoped to the connection's own tenant via
   * X-Forwarded-Host, and the API applies its per-tenant, per-collection read
   * policy (each v3 endpoint carries its own `RequireScope`). The probe carries
   * the client's credential and nothing else — never the bridge's instance key,
   * which would authenticate any anonymous caller as a service.
   *
   * Returns true when the API accepts the token for that tenant + endpoint.
   */
  async probeAccessToken(
    accessToken: string,
    tenantSlug: string,
    endpointPath: string,
  ): Promise<boolean> {
    if (!this.apiBaseUrl) {
      logger.warn('v3 namespace auth probe has no API base URL configured');
      return false;
    }
    try {
      const url = new URL(`${this.apiBaseUrl}${endpointPath}`);
      url.searchParams.set('count', '1');
      const probe = await fetch(url, {
        method: 'GET',
        headers: {
          Authorization: `Bearer ${accessToken}`,
          'X-Forwarded-Host': `${tenantSlug}.${this.baseDomain}`,
          // The API's response cache runs before auth and keys on the internal
          // Host, so a cached authenticated 200 could authorize an
          // unauthenticated probe.
          'Cache-Control': 'no-cache, no-store',
        },
        signal: AbortSignal.timeout(5000),
      });
      return probe.ok;
    } catch (error) {
      const reason = error instanceof Error ? error.message : String(error);
      logger.warn(`v3 namespace auth probe failed: ${reason}`);
      return false;
    }
  }

  /**
   * Nightscout v3 `/storage` namespace. AAPS connects to `<url>/storage` and,
   * on connect, emits `subscribe` with its access token and the collections it
   * wants. On a successful auth the socket joins a per-collection room and
   * subsequently receives `create` / `update` / `delete` events fanned out by
   * {@link broadcastStorageEvent}.
   *
   * Auth is in-band (the `subscribe` event), not at the handshake, because
   * AAPS sends the token as a JSON field — there is no signed ticket path for
   * native uploaders.
   */
  private setupStorageNamespace(): void {
    if (!this.io) return;
    this.storageNsp = this.io.of('/storage');

    // Admit the socket unauthorized; the tenant is resolved for room routing
    // but no room is joined until `subscribe` succeeds.
    this.storageNsp.use(async (socket, next) => {
      const tenantSlug = this.resolveNamespaceTenant(socket);
      if (!tenantSlug) {
        logger.warn(`Rejecting /storage connection ${socket.id}: no resolvable tenant`);
        return next(new Error('tenant_unresolved'));
      }
      socket.data.pendingTenantSlug = tenantSlug;
      next();
    });

    this.storageNsp.on('connection', (socket: Socket) => {
      logger.info(`v3 /storage client connected: ${socket.id}`);

      socket.on('subscribe', async (payload: unknown, ack?: (result: unknown) => void) => {
        await this.handleStorageSubscribe(socket, payload, ack);
      });

      socket.on('disconnect', (reason: string) => {
        logger.info(`v3 /storage client disconnected: ${socket.id}, reason: ${reason}`);
      });
    });
  }

  /**
   * Handle the v3 `/storage` `subscribe` event. Exposed for unit testing.
   *
   * Each requested collection is probed independently against its own v3 read
   * endpoint, so a token with read access to `entries` but not `treatments`
   * only joins the `entries` room. A token denied for every collection is
   * rejected outright — and the socket is disconnected so the client has to
   * re-establish the connection (going back through TLS/transport) rather than
   * immediately retrying `subscribe` as a credential-guessing oracle.
   */
  async handleStorageSubscribe(
    socket: Socket,
    payload: unknown,
    ack?: (result: unknown) => void,
  ): Promise<void> {
    const tenantSlug = socket.data.pendingTenantSlug as string | undefined;
    if (!tenantSlug) {
      ack?.({ success: false, message: 'no resolvable tenant' });
      socket.disconnect(true);
      return;
    }

    const message = (payload ?? {}) as { accessToken?: unknown; collections?: unknown };
    const accessToken = typeof message.accessToken === 'string' ? message.accessToken : undefined;
    if (!accessToken) {
      ack?.({ success: false, message: 'Missing or bad accessToken' });
      socket.disconnect(true);
      return;
    }

    // Normalize the requested collections to the known v3 set, preserving order
    // and dropping anything unrecognized.
    const requested = Array.isArray(message.collections)
      ? message.collections.filter((c): c is string => typeof c === 'string')
      : [];
    const candidateCollections = requested.length > 0
      ? requested.filter((c) => (KNOWN_STORAGE_COLLECTIONS as readonly string[]).includes(c))
      : [...KNOWN_STORAGE_COLLECTIONS];

    // Probe each collection's read endpoint independently so authorization is
    // per-collection, matching the API's per-endpoint RequireScope.
    const authorized: string[] = [];
    for (const collection of candidateCollections) {
      const endpoint = COLLECTION_READ_ENDPOINT[collection];
      const ok = await this.probeAccessToken(accessToken, tenantSlug, endpoint);
      if (ok) authorized.push(collection);
    }

    if (authorized.length === 0) {
      logger.warn(
        `/storage subscribe denied for ${socket.id} (tenant ${tenantSlug}): token not authorized for any collection`,
      );
      ack?.({ success: false, message: 'Unauthorized to receive any collection' });
      socket.disconnect(true);
      return;
    }

    for (const collection of authorized) {
      socket.join(storageRoom(tenantSlug, collection));
    }
    socket.data.tenantSlug = tenantSlug;
    socket.data.pendingTenantSlug = undefined;
    logger.info(
      `/storage client ${socket.id} subscribed for tenant ${tenantSlug}: ${authorized.join(', ')}`,
    );
    ack?.({ success: true, collections: authorized });
  }

  /**
   * Nightscout v3 `/alarm` namespace. AAPS connects to `<url>/alarm` and emits
   * `subscribe` with its access token; thereafter it receives `alarm`,
   * `urgent_alarm`, `announcement`, and `clear_alarm` events. It may also emit a
   * positional `ack` (level, group, silenceTime) to silence an alarm.
   *
   * `onAlarmAck` is invoked when a client acks an alarm; it defaults to a no-op
   * log because forwarding to the API's AlarmHub requires the per-tenant
   * SignalR client, which the server doesn't hold a reference to. Callers (the
   * bridge setup) can override it to wire up the forward.
   */
  onAlarmAck: (tenantSlug: string, level: number, group: string, silenceTime: number) => void =
    (tenantSlug, level, group, silenceTime) => {
      logger.info(
        `/alarm ack received (tenant ${tenantSlug}): level=${level} group=${group} silence=${silenceTime}ms (not forwarded)`,
      );
    };

  private setupAlarmNamespace(): void {
    if (!this.io) return;
    this.alarmNsp = this.io.of('/alarm');

    this.alarmNsp.use(async (socket, next) => {
      const tenantSlug = this.resolveNamespaceTenant(socket);
      if (!tenantSlug) {
        logger.warn(`Rejecting /alarm connection ${socket.id}: no resolvable tenant`);
        return next(new Error('tenant_unresolved'));
      }
      socket.data.pendingTenantSlug = tenantSlug;
      next();
    });

    this.alarmNsp.on('connection', (socket: Socket) => {
      logger.info(`v3 /alarm client connected: ${socket.id}`);

      socket.on('subscribe', async (payload: unknown, ack?: (result: unknown) => void) => {
        await this.handleAlarmSubscribe(socket, payload, ack);
      });

      // Positional ack: AAPS emits ("ack", level, group, silenceTime) — three
      // separate arguments, not a JSON object (matches cgm-remote-monitor).
      socket.on('ack', (level: unknown, group: unknown, silenceTime: unknown) => {
        const tenantSlug = socket.data.tenantSlug as string | undefined;
        if (!tenantSlug) return;
        this.onAlarmAck(
          tenantSlug,
          typeof level === 'number' ? level : Number(level) || 0,
          typeof group === 'string' ? group : String(group ?? ''),
          typeof silenceTime === 'number' ? silenceTime : Number(silenceTime) || 0,
        );
      });

      socket.on('disconnect', (reason: string) => {
        logger.info(`v3 /alarm client disconnected: ${socket.id}, reason: ${reason}`);
      });
    });
  }

  /**
   * Handle the v3 `/alarm` `subscribe` event. Exposed for unit testing.
   *
   * On failure the socket is disconnected so the client has to re-establish the
   * connection rather than immediately retrying `subscribe` as a
   * credential-guessing oracle.
   */
  async handleAlarmSubscribe(
    socket: Socket,
    payload: unknown,
    ack?: (result: unknown) => void,
  ): Promise<void> {
    const tenantSlug = socket.data.pendingTenantSlug as string | undefined;
    if (!tenantSlug) {
      ack?.({ success: false, message: 'no resolvable tenant' });
      socket.disconnect(true);
      return;
    }

    const message = (payload ?? {}) as { accessToken?: unknown };
    const accessToken = typeof message.accessToken === 'string' ? message.accessToken : undefined;
    if (!accessToken) {
      ack?.({ success: false, message: 'Missing or bad accessToken' });
      socket.disconnect(true);
      return;
    }

    // Probe the entries read endpoint — alarm subscription requires the same
    // tenant read access as a storage subscription.
    const authorized = await this.probeAccessToken(
      accessToken,
      tenantSlug,
      COLLECTION_READ_ENDPOINT.entries,
    );
    if (!authorized) {
      logger.warn(`/alarm subscribe denied for ${socket.id} (tenant ${tenantSlug})`);
      ack?.({ success: false, message: 'Missing or bad accessToken' });
      socket.disconnect(true);
      return;
    }

    socket.join(alarmRoom(tenantSlug));
    socket.data.tenantSlug = tenantSlug;
    socket.data.pendingTenantSlug = undefined;
    logger.info(`/alarm client ${socket.id} subscribed for tenant ${tenantSlug}`);
    ack?.({ success: true, message: 'Subscribed for alarms' });
  }

  async stop(): Promise<void> {
    if (this.io) {
      await this.io.close();
      logger.info('Socket.IO server stopped');
    }
  }
}

export default SocketIOServer;
