import { Server as SocketIOServerClass, Socket } from 'socket.io';
import { Server as HttpServer } from 'http';
import logger from './logger.js';
import type { ClientInfo, AlarmData, ServerStats } from '../types.js';
import { verifyHandshakeTicket, normalizeHandshakeHost } from './handshake-ticket.js';

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
  }

  broadcastAlarm(alarm: AlarmData, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    const eventName = alarm.level === 'urgent' ? 'urgent_alarm' : 'alarm';
    logger.debug(`Broadcasting ${eventName}${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit(eventName, alarm);
  }

  broadcastClearAlarm(tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    logger.debug(`Broadcasting clear_alarm${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('clear_alarm');
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

  async stop(): Promise<void> {
    if (this.io) {
      await this.io.close();
      logger.info('Socket.IO server stopped');
    }
  }
}

export default SocketIOServer;
