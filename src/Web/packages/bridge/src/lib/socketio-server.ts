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

  constructor(
    httpServer: HttpServer,
    config: SocketIOConfig = {},
    baseDomain: string,
    tenantSlugs: string[] = [],
    signingSecret: string = '',
  ) {
    this.httpServer = httpServer;
    this.baseDomain = baseDomain;
    this.tenantSlugs = tenantSlugs;
    this.signingSecret = signingSecret;
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
          pingInterval: this.config.pingInterval
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

      const token = (socket.handshake.auth as { token?: string } | undefined)?.token;
      const ticket = verifyHandshakeTicket(this.signingSecret, token);
      if (!ticket) {
        logger.warn(`Rejecting connection ${socket.id}: missing or invalid handshake ticket for tenant ${tenantSlug}`);
        return next(new Error('unauthorized'));
      }

      // Bind the ticket to the host the connection actually arrived on, so a
      // ticket minted for one tenant can't be replayed on another tenant's socket.
      if (ticket.h !== normalizeHandshakeHost(host!)) {
        logger.warn(`Rejecting connection ${socket.id}: handshake ticket host mismatch for tenant ${tenantSlug}`);
        return next(new Error('unauthorized'));
      }

      socket.data.tenantSlug = tenantSlug;
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

  /** Return the Socket.IO emit target: tenant room if scoped, or all clients. */
  private emitTarget(tenantSlug?: string) {
    if (!this.io) return null;
    return tenantSlug ? this.io.to(`tenant:${tenantSlug}`) : this.io;
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
    logger.info(`Broadcasting storage ${eventType} event to ${clientCount} connected clients${tenantSlug ? ` (tenant: ${tenantSlug})` : ''}`);

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
