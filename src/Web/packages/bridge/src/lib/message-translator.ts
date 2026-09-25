import logger from './logger.js';
import SocketIOServer from './socketio-server.js';
import { fieldsOf, type Payload } from './payload.js';

class MessageTranslator {
  private socketIOServer: SocketIOServer;
  private tenantSlug?: string;

  constructor(socketIOServer: SocketIOServer, tenantSlug?: string) {
    this.socketIOServer = socketIOServer;
    this.tenantSlug = tenantSlug;
  }

  handleDataUpdate(data: unknown): void {
    try {
      const translatedData = this.translateDataUpdate(data);
      this.socketIOServer.broadcastDataUpdate(translatedData, this.tenantSlug);
    } catch (error) {
      logger.error('Error translating data update:', error);
    }
  }

  handleAnnouncement(message: unknown): void {
    try {
      const translatedMessage = this.translateAnnouncement(message);
      this.socketIOServer.broadcastAnnouncement(translatedMessage, this.tenantSlug);
    } catch (error) {
      logger.error('Error translating announcement:', error);
    }
  }

  handleAlarm(alarm: unknown): void {
    try {
      const translatedAlarm = this.translateAlarm(alarm);
      this.socketIOServer.broadcastAlarm(translatedAlarm, this.tenantSlug);
    } catch (error) {
      logger.error('Error translating alarm:', error);
    }
  }

  handleClearAlarm(): void {
    try {
      this.socketIOServer.broadcastClearAlarm(this.tenantSlug);
    } catch (error) {
      logger.error('Error handling clear alarm:', error);
    }
  }

  handleNotification(notification: unknown): void {
    try {
      const translatedNotification = this.translateNotification(notification);
      this.socketIOServer.broadcastNotification(translatedNotification, this.tenantSlug);
    } catch (error) {
      logger.error('Error translating notification:', error);
    }
  }

  handleStatusUpdate(status: unknown): void {
    try {
      const translatedStatus = this.translateStatusUpdate(status);
      this.socketIOServer.broadcastStatusUpdate(translatedStatus, this.tenantSlug);
    } catch (error) {
      logger.error('Error translating status update:', error);
    }
  }

  handleStorageCreate(data: unknown): void {
    try {
      const translatedData = this.translateStorageEvent(data);
      this.socketIOServer.broadcastStorageEvent('create', translatedData, this.tenantSlug);
    } catch (error) {
      logger.error('Error translating storage create:', error);
    }
  }

  handleStorageUpdate(data: unknown): void {
    try {
      const translatedData = this.translateStorageEvent(data);
      this.socketIOServer.broadcastStorageEvent('update', translatedData, this.tenantSlug);
    } catch (error) {
      logger.error('Error translating storage update:', error);
    }
  }

  handleStorageDelete(data: unknown): void {
    try {
      const translatedData = this.translateStorageEvent(data);
      this.socketIOServer.broadcastStorageEvent('delete', translatedData, this.tenantSlug);
    } catch (error) {
      logger.error('Error translating storage delete:', error);
    }
  }

  handleNotificationCreated(data: unknown, subjectId?: string): void {
    try {
      this.socketIOServer.broadcastInAppNotification('notificationCreated', data, this.tenantSlug, subjectId);
    } catch (error) {
      logger.error('Error handling notification created:', error);
    }
  }

  handleNotificationArchived(data: unknown, subjectId?: string): void {
    try {
      this.socketIOServer.broadcastInAppNotification('notificationArchived', data, this.tenantSlug, subjectId);
    } catch (error) {
      logger.error('Error handling notification archived:', error);
    }
  }

  handleNotificationUpdated(data: unknown, subjectId?: string): void {
    try {
      this.socketIOServer.broadcastInAppNotification('notificationUpdated', data, this.tenantSlug, subjectId);
    } catch (error) {
      logger.error('Error handling notification updated:', error);
    }
  }

  handleTrackerUpdate(data: unknown): void {
    try {
      this.socketIOServer.broadcastTrackerUpdate(data, this.tenantSlug);
    } catch (error) {
      logger.error('Error handling tracker update:', error);
    }
  }

  handleSyncProgress(data: unknown): void {
    try {
      this.socketIOServer.broadcastSyncProgress(data, this.tenantSlug);
    } catch (error) {
      logger.error('Error handling sync progress:', error);
    }
  }

  handleConfigChanged(data: unknown): void {
    try {
      this.socketIOServer.broadcastConfigChanged(data, this.tenantSlug);
    } catch (error) {
      logger.error('Error handling config changed:', error);
    }
  }

  // Translation methods - these ensure compatibility with legacy Nightscout client expectations

  private translateDataUpdate(data: unknown): unknown {
    // Ensure the data structure matches what legacy clients expect
    if (Array.isArray(data)) {
      return data.map(item => this.translateSingleDataPoint(item));
    } else if (data && typeof data === 'object') {
      return this.translateSingleDataPoint(data);
    }
    return data;
  }

  private translateSingleDataPoint(data: unknown): Payload {
    const item = fieldsOf(data);
    // Ensure required fields are present for legacy compatibility
    return {
      _id: item._id || item.id,
      sgv: item.sgv || item.value,
      date: item.date || item.timestamp,
      dateString: item.dateString || new Date(dateInput(item.date || item.timestamp || Date.now())).toISOString(),
      trend: item.trend,
      direction: item.direction,
      filtered: item.filtered,
      unfiltered: item.unfiltered,
      rssi: item.rssi,
      noise: item.noise,
      type: item.type || 'sgv',
      ...item // Include any additional fields
    };
  }

  private translateAnnouncement(data: unknown): Payload {
    const message = fieldsOf(data);
    // Ensure announcement format matches legacy expectations
    return {
      message: message.message || message.text || String(data),
      title: message.title || 'Announcement',
      level: message.level || 'info',
      timestamp: message.timestamp || new Date().toISOString(),
      ...message
    };
  }

  private translateAlarm(data: unknown): Payload {
    const alarm = fieldsOf(data);
    // Ensure alarm format matches legacy expectations
    return {
      level: alarm.level || 'warn', // 'urgent', 'warn', 'info'
      title: alarm.title || 'Alarm',
      message: alarm.message,
      plugin: alarm.plugin || alarm.source,
      timestamp: alarm.timestamp || new Date().toISOString(),
      key: alarm.key || alarm.id,
      ...alarm
    };
  }

  private translateNotification(data: unknown): Payload {
    const notification = fieldsOf(data);
    // Ensure notification format matches legacy expectations
    return {
      title: notification.title,
      message: notification.message,
      level: notification.level || 'info',
      plugin: notification.plugin || notification.source,
      timestamp: notification.timestamp || new Date().toISOString(),
      ...notification
    };
  }

  private translateStatusUpdate(data: unknown): Payload {
    const status = fieldsOf(data);
    // Ensure status format matches legacy expectations
    return {
      status: status.status || status.state,
      message: status.message,
      timestamp: status.timestamp || new Date().toISOString(),
      ...status
    };
  }

  private translateStorageEvent(payload: unknown): Payload {
    const data = fieldsOf(payload);
    // Ensure storage event format matches legacy expectations
    // Legacy Nightscout expects { colName: 'entries', doc: {...} } format
    return {
      colName: data.colName || data.collection,
      doc: data.doc || data.document || payload,
      ...data
    };
  }

}

/**
 * The API sends a timestamp as mills or an ISO string. Anything else is read as a number,
 * which is how the Date constructor reads a non-string primitive.
 */
function dateInput(value: unknown): number | string | Date {
  if (typeof value === 'number' || typeof value === 'string' || value instanceof Date) return value;
  return Number(value);
}

export default MessageTranslator;
