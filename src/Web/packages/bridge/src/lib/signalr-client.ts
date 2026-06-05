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
  configHubUrl?: string;
  reconnectAttempts: number;
  reconnectDelay: number;
  maxReconnectDelay: number;
  instanceKey: string;
  /** Extra headers sent on every SignalR HTTP request (negotiate + WebSocket
   *  upgrade). Used to pass X-Forwarded-Host for tenant resolution. */
  connectionHeaders?: Record<string, string>;
}

function isSetupRequiredError(error: unknown): boolean {
  return error instanceof Error && error.message.includes('"setupRequired":true');
}

class SignalRClient {
  private messageHandler: MessageTranslator;
  private dataConnection: HubConnection | null = null;
  private alarmConnection: HubConnection | null = null;
  private configConnection: HubConnection | null = null;
  private reconnectAttempts: number = 0;
  private maxReconnectAttempts: number;
  private reconnectDelay: number;
  private maxReconnectDelay: number;
  private hubUrl: string;
  private alarmHubUrl?: string;
  private configHubUrl?: string;
  private instanceKey: string;
  private connectionHeaders: Record<string, string>;
  private isConnecting: boolean = false;

  constructor(messageHandler: MessageTranslator, config: SignalRConfig) {
    this.messageHandler = messageHandler;
    this.hubUrl = config.hubUrl;
    this.alarmHubUrl = config.alarmHubUrl;
    this.configHubUrl = config.configHubUrl;
    this.maxReconnectAttempts = config.reconnectAttempts;
    this.reconnectDelay = config.reconnectDelay;
    this.maxReconnectDelay = config.maxReconnectDelay;
    this.instanceKey = config.instanceKey;
    this.connectionHeaders = config.connectionHeaders ?? {};
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

      if (this.configHubUrl) {
        const keyHash = createHash("sha1")
          .update(this.instanceKey)
          .digest("hex");
        this.configConnection = this.buildConnection(this.configHubUrl, {
          // X-Instance-Service marks this as a genuine service call so the
          // API honors the instance key (a bare key is ignored).
          headers: {
            "X-Instance-Key": keyHash,
            "X-Instance-Service": "nocturne-bridge",
          },
        });
        this.setupConfigEventHandlers();

        await this.configConnection.start();
        logger.info("SignalR ConfigHub connection established");

        await this.subscribeToConfigHub();
      }

      this.reconnectAttempts = 0;
    } catch (error) {
      logger.error("Failed to connect to SignalR hub:", error);
      await this.handleReconnect(isSetupRequiredError(error));
    } finally {
      this.isConnecting = false;
    }
  }

  private buildConnection(
    hubUrl: string,
    options?: { headers?: Record<string, string> },
  ): HubConnection {
    const headers = { ...this.connectionHeaders, ...options?.headers };
    return new HubConnectionBuilder()
      .withUrl(hubUrl, Object.keys(headers).length > 0 ? { headers } : {})
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

  private setupConfigEventHandlers(): void {
    if (!this.configConnection) return;

    this.configConnection.onclose(() => {
      logger.warn("SignalR ConfigHub connection closed");
      this.handleReconnect();
    });

    this.configConnection.onreconnecting(() => {
      logger.info("SignalR ConfigHub connection lost, attempting to reconnect...");
    });

    this.configConnection.onreconnected(async () => {
      logger.info("SignalR ConfigHub connection reestablished");
      this.reconnectAttempts = 0;
      await this.subscribeToConfigHub();
    });

    this.configConnection.on("syncProgress", (data: any) => {
      logger.debug("Received syncProgress from SignalR:", data);
      this.messageHandler.handleSyncProgress(data);
    });

    this.configConnection.on("configChanged", (data: any) => {
      logger.debug("Received configChanged from SignalR:", data);
      this.messageHandler.handleConfigChanged(data);
    });
  }

  private async subscribeToConfigHub(): Promise<void> {
    if (!this.configConnection) return;

    try {
      logger.info("Subscribing to all config changes...");
      await this.configConnection.invoke("SubscribeAll");
      logger.info("Successfully subscribed to ConfigHub");
    } catch (error) {
      logger.error("Error subscribing to ConfigHub:", error);
    }
  }

  private async authenticateWithDataHub(): Promise<void> {
    if (!this.dataConnection) return;
    if (!this.instanceKey) {
      throw new Error(
        "INSTANCE_KEY is not configured for the websocket bridge",
      );
    }
    try {
      const secretHash = createHash("sha1")
        .update(this.instanceKey)
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
    if (!this.instanceKey) {
      throw new Error(
        "INSTANCE_KEY is not configured for the websocket bridge",
      );
    }

    try {
      const secretHash = createHash("sha1")
        .update(this.instanceKey)
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

  private async handleReconnect(isSetupRequired = false): Promise<void> {
    if (!isSetupRequired && this.reconnectAttempts >= this.maxReconnectAttempts) {
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

    const attemptLabel = isSetupRequired
      ? `setup pending, attempt ${this.reconnectAttempts}`
      : `attempt ${this.reconnectAttempts}/${this.maxReconnectAttempts}`;
    logger.info(
      `Attempting to reconnect to SignalR hub in ${delay}ms (${attemptLabel})`,
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

    if (this.configConnection) {
      try {
        await this.configConnection.stop();
        logger.info("SignalR ConfigHub connection stopped");
      } catch (error) {
        logger.error("Error stopping SignalR ConfigHub connection:", error);
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
    const configConnected =
      !this.configHubUrl ||
      (this.configConnection !== null &&
        this.configConnection.state === "Connected");

    return dataConnected && alarmConnected && configConnected;
  }
}

export default SignalRClient;
