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
  reconnectAttempts: number;
  reconnectDelay: number;
  maxReconnectDelay: number;
  apiSecret: string;
}

class SignalRClient {
  private messageHandler: MessageTranslator;
  private connection: HubConnection | null = null;
  private reconnectAttempts: number = 0;
  private maxReconnectAttempts: number;
  private reconnectDelay: number;
  private maxReconnectDelay: number;
  private hubUrl: string;
  private apiSecret: string;
  private isConnecting: boolean = false;

  constructor(messageHandler: MessageTranslator, config: SignalRConfig) {
    this.messageHandler = messageHandler;
    this.hubUrl = config.hubUrl;
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
      this.connection = new HubConnectionBuilder()
        .withUrl(this.hubUrl)
        .withAutomaticReconnect({
          nextRetryDelayInMilliseconds: (retryContext) => {
            const delay = Math.min(
              this.reconnectDelay *
                Math.pow(2, retryContext.previousRetryCount),
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

      // Set up event handlers
      this.setupEventHandlers();

      // Start the connection
      await this.connection.start();
      logger.info("SignalR connection established");

      // Authenticate with the hub to join the "authorized" group
      await this.authenticateWithHub();

      // Subscribe to storage collections to receive create/update/delete events
      await this.subscribeToStorageCollections();

      this.reconnectAttempts = 0;
      this.isConnecting = false;
    } catch (error) {
      logger.error("Failed to connect to SignalR hub:", error);
      this.isConnecting = false;
      await this.handleReconnect();
    }
  }

  private setupEventHandlers(): void {
    if (!this.connection) return;

    // Handle connection state changes
    this.connection.onclose(() => {
      logger.warn("SignalR connection closed");
      this.handleReconnect();
    });

    this.connection.onreconnecting(() => {
      logger.info("SignalR connection lost, attempting to reconnect...");
    });

    this.connection.onreconnected(async () => {
      logger.info("SignalR connection reestablished");
      this.reconnectAttempts = 0;

      // Re-authenticate and re-subscribe after reconnection
      await this.authenticateWithHub();
      await this.subscribeToStorageCollections();
    });

    // Handle incoming messages from SignalR hub
    this.connection.on("dataUpdate", (data: any) => {
      logger.debug("Received dataUpdate from SignalR:", data);
      this.messageHandler.handleDataUpdate(data);
    });

    this.connection.on("announcement", (message: any) => {
      logger.debug("Received announcement from SignalR:", message);
      this.messageHandler.handleAnnouncement(message);
    });

    this.connection.on("alarm", (alarm: any) => {
      logger.debug("Received alarm from SignalR:", alarm);
      this.messageHandler.handleAlarm(alarm);
    });

    this.connection.on("clear_alarm", () => {
      logger.debug("Received clear_alarm from SignalR");
      this.messageHandler.handleClearAlarm();
    });

    this.connection.on("notification", (notification: any) => {
      logger.debug("Received notification from SignalR:", notification);
      this.messageHandler.handleNotification(notification);
    });

    this.connection.on("statusUpdate", (status: any) => {
      logger.debug("Received statusUpdate from SignalR:", status);
      this.messageHandler.handleStatusUpdate(status);
    });

    // Handle storage events (create, update, delete)
    // These method names must match the WebSocketEvents enum in C#
    this.connection.on("create", (data: any) => {
      logger.debug("Received create from SignalR:", data);
      this.messageHandler.handleStorageCreate(data);
    });

    this.connection.on("update", (data: any) => {
      logger.debug("Received update from SignalR:", data);
      this.messageHandler.handleStorageUpdate(data);
    });

    this.connection.on("delete", (data: any) => {
      logger.debug("Received delete from SignalR:", data);
      this.messageHandler.handleStorageDelete(data);
    });

    // Handle in-app notification events
    this.connection.on("notificationCreated", (data: any) => {
      logger.debug("Received notificationCreated from SignalR:", data);
      this.messageHandler.handleNotificationCreated(data);
    });

    this.connection.on("notificationArchived", (data: any) => {
      logger.debug("Received notificationArchived from SignalR:", data);
      this.messageHandler.handleNotificationArchived(data);
    });

    this.connection.on("notificationUpdated", (data: any) => {
      logger.debug("Received notificationUpdated from SignalR:", data);
      this.messageHandler.handleNotificationUpdated(data);
    });
  }

  private async authenticateWithHub(): Promise<void> {
    if (!this.connection) return;
    if (!this.apiSecret) {
      throw new Error(
        "API_SECRET is not configured for the websocket bridge",
      );
    }
    try {
      // Hash the API secret with SHA1 to match Nightscout authentication
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
      const authResult = await this.connection.invoke("Authorize", authData);

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
    if (!this.connection) return;

    try {
      // Subscribe to all storage collections
      const collections = ["entries", "treatments", "devicestatus", "profiles"];

      logger.info("Subscribing to storage collections:", collections);
      const subscribeResult = await this.connection.invoke("Subscribe", {
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
    if (this.connection) {
      try {
        await this.connection.stop();
        logger.info("SignalR connection stopped");
      } catch (error) {
        logger.error("Error stopping SignalR connection:", error);
      }
    }
  }

  isConnected(): boolean {
    return this.connection !== null && this.connection.state === "Connected";
  }
}

export default SignalRClient;
