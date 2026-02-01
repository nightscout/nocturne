import {
  HubConnection,
  HubConnectionBuilder,
  LogLevel,
} from "@microsoft/signalr";
import { createHash } from "crypto";
import logger from "./logger.js";
import MessageTranslator from "./message-translator.js";

interface SignalRConfig {
  hubUrl: string;
  alarmHubUrl?: string;
  reconnectAttempts: number;
  reconnectDelay: number;
  maxReconnectDelay: number;
  apiSecret: string;
}

class SignalRClient {
  private messageHandler: MessageTranslator;
  private dataConnection: HubConnection | null = null;
  private alarmConnection: HubConnection | null = null;
  private reconnectAttempts: number = 0;
  private maxReconnectAttempts: number;
  private reconnectDelay: number;
  private maxReconnectDelay: number;
  private hubUrl: string;
  private alarmHubUrl?: string;
  private apiSecret: string;
  private isConnecting: boolean = false;

  constructor(messageHandler: MessageTranslator, config: SignalRConfig) {
    this.messageHandler = messageHandler;
    this.hubUrl = config.hubUrl;
    this.alarmHubUrl = config.alarmHubUrl;
    this.maxReconnectAttempts = config.reconnectAttempts;
    this.reconnectDelay = config.reconnectDelay;
    this.maxReconnectDelay = config.maxReconnectDelay;
    this.apiSecret = config.apiSecret;
  }

  async connect(): Promise<void> {
    if (this.isConnecting) {
      logger.warn("SignalR connection attempt already in progress");
      return;
    }

    this.isConnecting = true;

    try {
      this.dataConnection = this.buildConnection(this.hubUrl);
      this.setupDataEventHandlers();

      await this.dataConnection.start();
      logger.info("SignalR DataHub connection established");

      await this.authenticateWithDataHub();
      await this.subscribeToStorageCollections();

      if (this.alarmHubUrl) {
        this.alarmConnection = this.buildConnection(this.alarmHubUrl);
        this.setupAlarmEventHandlers();

        await this.alarmConnection.start();
        logger.info("SignalR AlarmHub connection established");

        await this.subscribeToAlarmHub();
      }

      this.reconnectAttempts = 0;
    } catch (error) {
      logger.error("Failed to connect to SignalR hub:", error);
      await this.handleReconnect();
    } finally {
      this.isConnecting = false;
    }
  }

  private buildConnection(hubUrl: string): HubConnection {
    return new HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          const delay = Math.min(
            this.reconnectDelay * Math.pow(2, retryContext.previousRetryCount),
            this.maxReconnectDelay,
          );
          logger.info(
            `SignalR reconnect attempt ${
              retryContext.previousRetryCount + 1
            } in ${delay}ms`,
          );
          return delay;
        },
      })
      .configureLogging(LogLevel.Information)
      .build();
  }

  private setupDataEventHandlers(): void {
    if (!this.dataConnection) return;

    this.dataConnection.onclose(() => {
      logger.warn("SignalR DataHub connection closed");
      this.handleReconnect();
    });

    this.dataConnection.onreconnecting(() => {
      logger.info("SignalR DataHub connection lost, attempting to reconnect...");
    });

    this.dataConnection.onreconnected(async () => {
      logger.info("SignalR DataHub connection reestablished");
      this.reconnectAttempts = 0;

      await this.authenticateWithDataHub();
      await this.subscribeToStorageCollections();
    });

    this.dataConnection.on("dataUpdate", (data: any) => {
      logger.debug("Received dataUpdate from SignalR:", data);
      this.messageHandler.handleDataUpdate(data);
    });

    this.dataConnection.on("announcement", (message: any) => {
      logger.debug("Received announcement from SignalR:", message);
      this.messageHandler.handleAnnouncement(message);
    });

    this.dataConnection.on("notification", (notification: any) => {
      logger.debug("Received notification from SignalR:", notification);
      this.messageHandler.handleNotification(notification);
    });

    this.dataConnection.on("statusUpdate", (status: any) => {
      logger.debug("Received statusUpdate from SignalR:", status);
      this.messageHandler.handleStatusUpdate(status);
    });

    this.dataConnection.on("create", (data: any) => {
      logger.debug("Received create from SignalR:", data);
      this.messageHandler.handleStorageCreate(data);
    });

    this.dataConnection.on("update", (data: any) => {
      logger.debug("Received update from SignalR:", data);
      this.messageHandler.handleStorageUpdate(data);
    });

    this.dataConnection.on("delete", (data: any) => {
      logger.debug("Received delete from SignalR:", data);
      this.messageHandler.handleStorageDelete(data);
    });

    // Handle in-app notification events
    this.dataConnection.on("notificationCreated", (data: any) => {
      logger.debug("Received notificationCreated from SignalR:", data);
      this.messageHandler.handleNotificationCreated(data);
    });

    this.dataConnection.on("notificationArchived", (data: any) => {
      logger.debug("Received notificationArchived from SignalR:", data);
      this.messageHandler.handleNotificationArchived(data);
    });

    this.dataConnection.on("notificationUpdated", (data: any) => {
      logger.debug("Received notificationUpdated from SignalR:", data);
      this.messageHandler.handleNotificationUpdated(data);
    });
  }

  private setupAlarmEventHandlers(): void {
    if (!this.alarmConnection) return;

    this.alarmConnection.onclose(() => {
      logger.warn("SignalR AlarmHub connection closed");
      this.handleReconnect();
    });

    this.alarmConnection.onreconnecting(() => {
      logger.info("SignalR AlarmHub connection lost, attempting to reconnect...");
    });

    this.alarmConnection.onreconnected(async () => {
      logger.info("SignalR AlarmHub connection reestablished");
      this.reconnectAttempts = 0;

      await this.subscribeToAlarmHub();
    });

    this.alarmConnection.on("alarm", (alarm: any) => {
      logger.debug("Received alarm from SignalR:", alarm);
      this.messageHandler.handleAlarm(alarm);
    });

    this.alarmConnection.on("urgent_alarm", (alarm: any) => {
      logger.debug("Received urgent_alarm from SignalR:", alarm);
      this.messageHandler.handleAlarm(alarm);
    });

    this.alarmConnection.on("clear_alarm", () => {
      logger.debug("Received clear_alarm from SignalR");
      this.messageHandler.handleClearAlarm();
    });
  }

  private async authenticateWithDataHub(): Promise<void> {
    if (!this.dataConnection) return;
    if (!this.apiSecret) {
      throw new Error(
        "API_SECRET is not configured for the websocket bridge",
      );
    }
    try {
      const secretHash = createHash("sha1")
        .update(this.apiSecret)
        .digest("hex")
        .toLowerCase();

      const authData = {
        client: "websocket-bridge",
        secret: secretHash,
        history: 24,
      };

      logger.info("Authenticating with SignalR DataHub...");
      const authResult = await this.dataConnection.invoke("Authorize", authData);

      if (authResult?.success) {
        logger.info("Successfully authenticated with SignalR DataHub");
      } else {
        logger.warn("SignalR DataHub authentication failed:", authResult);
      }
    } catch (error) {
      logger.error("Error authenticating with SignalR DataHub:", error);
    }
  }

  private async subscribeToStorageCollections(): Promise<void> {
    if (!this.dataConnection) return;

    try {
      const collections = ["entries", "treatments", "devicestatus", "profiles"];

      logger.info("Subscribing to storage collections:", collections);
      const subscribeResult = await this.dataConnection.invoke("Subscribe", {
        collections: collections,
      });

      if (subscribeResult?.success) {
        logger.info(
          "Successfully subscribed to storage collections:",
          subscribeResult.collections,
        );
      } else {
        logger.warn(
          "Failed to subscribe to some storage collections:",
          subscribeResult,
        );
      }
    } catch (error) {
      logger.error("Error subscribing to storage collections:", error);
    }
  }

  private async subscribeToAlarmHub(): Promise<void> {
    if (!this.alarmConnection) return;
    if (!this.apiSecret) {
      throw new Error(
        "API_SECRET is not configured for the websocket bridge",
      );
    }

    try {
      const secretHash = createHash("sha1")
        .update(this.apiSecret)
        .digest("hex")
        .toLowerCase();

      logger.info("Subscribing to SignalR AlarmHub...");
      const subscribeResult = await this.alarmConnection.invoke("Subscribe", {
        secret: secretHash,
      });

      if (subscribeResult?.success) {
        logger.info("Successfully subscribed to SignalR AlarmHub");
      } else {
        logger.warn("SignalR AlarmHub subscription failed:", subscribeResult);
      }
    } catch (error) {
      logger.error("Error subscribing to SignalR AlarmHub:", error);
    }
  }

  private async handleReconnect(): Promise<void> {
    if (this.reconnectAttempts >= this.maxReconnectAttempts) {
      logger.error(
        `Maximum reconnection attempts (${this.maxReconnectAttempts}) exceeded`,
      );
      return;
    }

    this.reconnectAttempts++;
    const delay = Math.min(
      this.reconnectDelay * Math.pow(2, this.reconnectAttempts - 1),
      this.maxReconnectDelay,
    );

    logger.info(
      `Attempting to reconnect to SignalR hub in ${delay}ms (attempt ${this.reconnectAttempts}/${this.maxReconnectAttempts})`,
    );

    setTimeout(() => {
      this.connect();
    }, delay);
  }

  async disconnect(): Promise<void> {
    if (this.dataConnection) {
      try {
        await this.dataConnection.stop();
        logger.info("SignalR DataHub connection stopped");
      } catch (error) {
        logger.error("Error stopping SignalR DataHub connection:", error);
      }
    }

    if (this.alarmConnection) {
      try {
        await this.alarmConnection.stop();
        logger.info("SignalR AlarmHub connection stopped");
      } catch (error) {
        logger.error("Error stopping SignalR AlarmHub connection:", error);
      }
    }
  }

  isConnected(): boolean {
    const dataConnected =
      this.dataConnection !== null && this.dataConnection.state === "Connected";
    const alarmConnected =
      !this.alarmHubUrl ||
      (this.alarmConnection !== null &&
        this.alarmConnection.state === "Connected");

    return dataConnected && alarmConnected;
  }
}

export default SignalRClient;
