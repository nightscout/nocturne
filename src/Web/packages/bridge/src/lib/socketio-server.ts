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

  constructor(httpServer: HttpServer, config: SocketIOConfig = {}) {
    this.httpServer = httpServer;
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

  // Methods to broadcast messages to clients
  broadcastDataUpdate(data: any): void {
    if (!this.io) return;

    logger.debug('Broadcasting dataUpdate to all clients');
    this.io.emit('dataUpdate', data);
  }

  broadcastAnnouncement(message: any): void {
    if (!this.io) return;

    logger.debug('Broadcasting announcement to all clients');
    this.io.emit('announcement', message);
  }

  broadcastAlarm(alarm: AlarmData): void {
    if (!this.io) return;

    const eventName = alarm.level === 'urgent' ? 'urgent_alarm' : 'alarm';
    logger.debug(`Broadcasting ${eventName} to all clients`);
    this.io.emit(eventName, alarm);
  }

  broadcastClearAlarm(): void {
    if (!this.io) return;

    logger.debug('Broadcasting clear_alarm to all clients');
    this.io.emit('clear_alarm');
  }

  broadcastNotification(notification: any): void {
    if (!this.io) return;

    logger.debug('Broadcasting notification to all clients');
    this.io.emit('notification', notification);
  }

  broadcastStatusUpdate(status: any): void {
    if (!this.io) return;

    logger.debug('Broadcasting status update to all clients');
    this.io.emit('status', status);
  }

  broadcastStorageEvent(eventType: 'create' | 'update' | 'delete', data: any): void {
    if (!this.io) return;

    const clientCount = this.clients.size;
    logger.info(`Broadcasting storage ${eventType} event to ${clientCount} connected clients`);

    if (clientCount === 0) {
      logger.warn('No Socket.IO clients connected - events will not be delivered to frontend');
    }

    this.io.emit(eventType, data);
  }

  broadcastInAppNotification(eventType: 'notificationCreated' | 'notificationArchived' | 'notificationUpdated', data: any): void {
    if (!this.io) return;

    logger.debug(`Broadcasting ${eventType} to all clients`);
    this.io.emit(eventType, data);
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
