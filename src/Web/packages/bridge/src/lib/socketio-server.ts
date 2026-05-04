import { Server as SocketIOServerClass, Socket } from 'socket.io';
import { Server as HttpServer } from 'http';
import logger from './logger.js';
import type { ClientInfo, AlarmData, ServerStats } from '../types.js';

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

class SocketIOServer {
  private io: SocketIOServerClass | null = null;
  private httpServer: HttpServer;
  private clients: Map<string, ClientInfo> = new Map();
  private config: SocketIOConfig;
  private baseDomain?: string;
  private tenantSlugs: string[];

  constructor(
    httpServer: HttpServer,
    config: SocketIOConfig = {},
    baseDomain?: string,
    tenantSlugs: string[] = [],
  ) {
    this.httpServer = httpServer;
    this.baseDomain = baseDomain;
    this.tenantSlugs = tenantSlugs;
    this.config = {
      cors: config.cors || {
        origin: '*',
        methods: ['GET', 'POST'],
        credentials: true
      },
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

        this.setupEventHandlers();

        logger.info('Socket.IO server attached to HTTP server');
        resolve();

      } catch (error) {
        logger.error('Failed to start Socket.IO server:', error);
        reject(error);
      }
    });
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

      // In multi-tenant mode, join the client to a tenant-specific room
      // based on the X-Forwarded-Host header set by the reverse proxy.
      if (this.baseDomain) {
        const tenantSlug = this.extractTenantSlug(socket);
        if (tenantSlug) {
          socket.join(`tenant:${tenantSlug}`);
          logger.info(`Client ${clientId} joined tenant room: ${tenantSlug}`);
        } else {
          logger.warn(`Client ${clientId} connected without resolvable tenant`);
        }
      }

      // Handle client disconnection
      socket.on('disconnect', (reason: string) => {
        this.clients.delete(clientId);
        logger.info(`Client disconnected: ${clientId}, reason: ${reason}`);
        logger.debug(`Total connected clients: ${this.clients.size}`);
      });

      // Handle client authentication if needed
      socket.on('authenticate', () => {
        logger.debug(`Client ${clientId} attempting authentication`);
        // TODO: Implement authentication logic if required
        socket.emit('authenticated', { success: true });
      });

      // Handle client joining rooms (for targeted messaging)
      socket.on('join', (room: string) => {
        socket.join(room);
        logger.debug(`Client ${clientId} joined room: ${room}`);
      });

      socket.on('leave', (room: string) => {
        socket.leave(room);
        logger.debug(`Client ${clientId} left room: ${room}`);
      });

      // Send initial connection acknowledgment
      socket.emit('connect_ack', {
        clientId: clientId,
        serverTime: new Date().toISOString(),
        version: '1.0.0'
      });
    });
  }

  /** Extract tenant slug from the Socket.IO handshake headers.
   *  Checks X-Forwarded-Host first (set by YARP's X-Forwarded transform),
   *  then falls back to Host (preserved by RequestHeaderOriginalHost).
   *  Apex-domain connections fall back to the sole tenant when exactly one
   *  exists, mirroring the API's TenantResolutionMiddleware behavior so
   *  self-hosted single-tenant deployments work without a subdomain. */
  private extractTenantSlug(socket: Socket): string | null {
    if (!this.baseDomain) return null;

    const forwardedHost = socket.handshake.headers['x-forwarded-host'];
    const rawHost = socket.handshake.headers['host'];
    const candidate = Array.isArray(forwardedHost) ? forwardedHost[0] : (forwardedHost || rawHost);
    if (!candidate) return null;

    const hostname = candidate.split(':')[0];
    const baseDomainHost = this.baseDomain.split(':')[0];

    if (hostname.endsWith(`.${baseDomainHost}`)) {
      const slug = hostname.slice(0, -(baseDomainHost.length + 1));
      return slug || null;
    }

    if (hostname === baseDomainHost && this.tenantSlugs.length === 1) {
      return this.tenantSlugs[0];
    }

    return null;
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
