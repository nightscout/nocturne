// Real-time data store using Svelte 5 Runes and WebSocket integration
import { WebSocketClient } from "$lib/websocket/websocket-client.svelte";
import type {
  Entry,
  Treatment,
  WebSocketConfig,
  DataUpdateEvent,
  StorageEvent,
  AnnouncementEvent,
  AlarmEvent,
} from "$lib/websocket/types";
import type { DeviceStatus, Profile } from "$lib/api";
import { toast } from "svelte-sonner";
import { getContext, setContext } from "svelte";
import { getApiClient } from "$lib/api/client";
import { processPillsData, type ProcessedPillsData } from "$lib/data/pills-processor";

const REALTIME_STORE_KEY = Symbol("realtime-store");

export class RealtimeStore {
  private websocketClient!: WebSocketClient;
  private initialized = false;

  /** Reactive state using Svelte 5 runes */
  entries = $state<Entry[]>([]);
  treatments = $state<Treatment[]>([]);
  deviceStatuses = $state<DeviceStatus[]>([]);
  profile = $state<Profile | null>(null);

  /** Connection state (with safe initialization) */
  connectionStatus = $derived(
    this.websocketClient?.connectionStatus || "disconnected"
  );
  isConnected = $derived(this.websocketClient?.isConnected || false);
  connectionError = $derived(this.websocketClient?.lastError || null);
  connectionStats = $derived(
    this.websocketClient?.stats || {
      connectedClients: 0,
      uptime: 0,
      serverPort: 0,
      messageCount: 0,
      reconnectCount: 0,
    }
  );

  /** Latest glucose data computations */
  currentEntry = $derived.by(() => {
    const sorted = [...this.entries].sort(
      (a, b) => (b.mills || 0) - (a.mills || 0)
    );
    return sorted[0] || null;
  });

  demoMode = $derived(this.entries.some((e) => e.data_source === "demo-service"));

  previousEntry = $derived.by(() => {
    const sorted = [...this.entries].sort(
      (a, b) => (b.mills || 0) - (a.mills || 0)
    );
    return sorted[1] || null;
  });

  /** Current glucose values */
  currentBG = $derived(this.currentEntry?.sgv ?? this.currentEntry?.mgdl ?? 0);
  previousBG = $derived(
    this.previousEntry?.sgv ?? this.previousEntry?.mgdl ?? 0
  );

  /** Delta calculation - prefer entry delta, fallback to computed */
  bgDelta = $derived.by(() => {
    if (this.currentEntry?.delta !== undefined) {
      return this.currentEntry.delta;
    }
    return this.currentBG - this.previousBG;
  });

  /** Direction and trend */
  direction = $derived(this.currentEntry?.direction || "Flat");

  /** Time since last update */
  lastUpdated = $derived(this.currentEntry?.mills || Date.now());
  timeSinceUpdate = $derived.by(() => {
    return Date.now() - this.lastUpdated;
  });

  /** Recent treatments */
  recentTreatments = $derived.by(() => {
    const oneDayAgo = Date.now() - 24 * 60 * 60 * 1000;
    return this.treatments
      .filter((t) => (t.mills || 0) > oneDayAgo)
      .sort((a, b) => (b.mills || 0) - (a.mills || 0));
  });

  /** Processed pills data (COB, IOB, CAGE, SAGE, Loop, Basal) */
  pillsData = $derived.by((): ProcessedPillsData => {
    return processPillsData(
      this.deviceStatuses,
      this.treatments,
      this.profile,
      { units: 'mmol/L' } // TODO: Get from settings
    );
  });

  constructor(config: WebSocketConfig) {
    this.websocketClient = new WebSocketClient(config);
    this.setupEventHandlers();
  }

  /** Initialize WebSocket connection - data will be populated via real-time events */
  async initialize(): Promise<void> {
    if (this.initialized) {
      return;
    }

    // Skip if WebSocket URL is not available (SSR scenario)
    if (!this.websocketClient.hasValidUrl()) {
      return;
    }

    // Start with empty data
    this.entries = [];
    this.treatments = [];
    this.deviceStatuses = [];
    this.profile = null;

    try {
      // Fetch historical data using the properly configured API client
      const apiClient = getApiClient();
      const [historicalEntries, historicalTreatments, deviceStatusData, profileData] = await Promise.all([
        apiClient.entries.getEntries2(undefined, 1000),
        apiClient.treatments.getTreatments2(undefined, 500),
        apiClient.deviceStatus.getDeviceStatus2(undefined, 100).catch(() => []),
        apiClient.profile.getProfiles2(1).catch(() => []),
      ]);

      if (historicalEntries && historicalEntries.length > 0) {
        this.entries = historicalEntries.sort(
          (a: Entry, b: Entry) => (b.mills || 0) - (a.mills || 0)
        );
      }

      if (historicalTreatments && historicalTreatments.length > 0) {
        this.treatments = historicalTreatments.sort(
          (a: Treatment, b: Treatment) => (b.mills || 0) - (a.mills || 0)
        );
      }

      if (deviceStatusData && deviceStatusData.length > 0) {
        this.deviceStatuses = deviceStatusData.sort(
          (a: DeviceStatus, b: DeviceStatus) => (b.mills || 0) - (a.mills || 0)
        );
      }

      if (profileData && profileData.length > 0) {
        this.profile = profileData[0];
      }
    } catch (error) {
      console.error("Failed to fetch historical data:", error);
      toast.error("Failed to load historical data");
    }

    // Connect to WebSocket bridge
    this.websocketClient.connect();
    this.initialized = true;
  }

  /** Setup WebSocket event handlers */
  private setupEventHandlers(): void {
    this.websocketClient.on("connect", () => {
      toast.success("Connected to real-time data");
    });

    this.websocketClient.on("disconnect", () => {
      toast.warning("Real-time data disconnected");
    });

    this.websocketClient.on("connect_error", () => {
      toast.error("Failed to connect to real-time data");
    });

    this.websocketClient.on("dataUpdate", (event: DataUpdateEvent) => {
      this.handleDataUpdate(event);
    });

    this.websocketClient.on("create", (event: StorageEvent) => {
      this.handleCreate(event);
    });

    this.websocketClient.on("update", (event: StorageEvent) => {
      this.handleUpdate(event);
    });

    this.websocketClient.on("delete", (event: StorageEvent) => {
      this.handleDelete(event);
    });

    this.websocketClient.on("announcement", (event: AnnouncementEvent) => {
      this.handleAnnouncement(event);
    });

    this.websocketClient.on("alarm", (event: AlarmEvent) => {
      this.handleAlarm(event);
    });

    this.websocketClient.on("clear_alarm", () => {
      this.handleClearAlarm();
    });
  }

  /** Handle real-time data updates */
  private handleDataUpdate(event: DataUpdateEvent): void {
    if (!Array.isArray(event.data)) return;

    // Merge new entries with existing ones, avoiding duplicates
    const newEntries = event.data.filter(
      (newEntry) =>
        !this.entries.some(
          (existing) =>
            existing._id === newEntry._id ||
            (existing.mills === newEntry.mills && existing.sgv === newEntry.sgv)
        )
    );

    if (newEntries.length > 0) {
      this.entries = [...this.entries, ...newEntries]
        .sort((a, b) => (b.mills || 0) - (a.mills || 0))
        .slice(0, 1000); // Keep last 1000 entries
    }
  }

  /** Handle storage create events */
  private handleCreate(event: StorageEvent): void {
    const { colName, doc } = event;

    if (colName === "entries" && this.isEntry(doc)) {
      // Check for duplicates
      const exists = this.entries.some(
        (entry) =>
          entry._id === doc._id ||
          (entry.mills === doc.mills && entry.sgv === doc.sgv)
      );

      if (!exists) {
        this.entries = [doc, ...this.entries]
          .sort((a, b) => (b.mills || 0) - (a.mills || 0))
          .slice(0, 1000);
      }
    } else if (colName === "treatments" && this.isTreatment(doc)) {
      const exists = this.treatments.some(
        (treatment) =>
          treatment._id === doc._id ||
          (treatment.mills === doc.mills &&
            treatment.eventType === doc.eventType)
      );

      if (!exists) {
        this.treatments = [doc, ...this.treatments]
          .sort((a, b) => (b.mills || 0) - (a.mills || 0))
          .slice(0, 500);
      }
    } else if (colName === "devicestatus" && this.isDeviceStatus(doc)) {
      const exists = this.deviceStatuses.some(
        (ds) => ds._id === doc._id
      );

      if (!exists) {
        this.deviceStatuses = [doc, ...this.deviceStatuses]
          .sort((a, b) => (b.mills || 0) - (a.mills || 0))
          .slice(0, 100);
      }
    }
  }

  /** Handle storage update events */
  private handleUpdate(event: StorageEvent): void {
    const { colName, doc } = event;

    if (colName === "entries" && this.isEntry(doc)) {
      const index = this.entries.findIndex((entry) => entry._id === doc._id);
      if (index !== -1) {
        this.entries = [
          ...this.entries.slice(0, index),
          doc,
          ...this.entries.slice(index + 1),
        ];
      } else {
        // If not found, treat as create
        this.handleCreate(event);
      }
    } else if (colName === "treatments" && this.isTreatment(doc)) {
      const index = this.treatments.findIndex(
        (treatment) => treatment._id === doc._id
      );
      if (index !== -1) {
        this.treatments = [
          ...this.treatments.slice(0, index),
          doc,
          ...this.treatments.slice(index + 1),
        ];
      } else {
        this.handleCreate(event);
      }
    }
  }

  /** Handle storage delete events */
  private handleDelete(event: StorageEvent): void {
    const { colName, doc } = event;

    if (colName === "entries") {
      this.entries = this.entries.filter((entry) => entry._id !== doc._id);
    } else if (colName === "treatments") {
      this.treatments = this.treatments.filter(
        (treatment) => treatment._id !== doc._id
      );
    }
  }

  /** Handle system announcements */
  private handleAnnouncement(event: AnnouncementEvent): void {
    const level =
      event.level === "warn"
        ? "warning"
        : event.level === "error"
          ? "error"
          : "info";

    toast[level](event.message, {
      description: event.title !== "Announcement" ? event.title : undefined,
    });
  }

  /** Handle alarms */
  private handleAlarm(event: AlarmEvent): void {
    const isUrgent = event.level === "urgent";
    const toastMethod = isUrgent ? "error" : "warning";

    toast[toastMethod](event.message || "Glucose alarm", {
      description: event.title,
      duration: isUrgent ? 10000 : 5000,
    });
  }

  /** Handle alarm clearing */
  private handleClearAlarm(): void {
    toast.success("Glucose alarm cleared");
  }

  /* Type guards for runtime type checking */
  private isEntry(obj: any): obj is Entry {
    return (
      obj &&
      typeof obj === "object" &&
      ("sgv" in obj || "mgdl" in obj || "mmol" in obj)
    );
  }

  private isTreatment(obj: any): obj is Treatment {
    return obj && typeof obj === "object" && "eventType" in obj;
  }

  private isDeviceStatus(obj: any): obj is DeviceStatus {
    return (
      obj &&
      typeof obj === "object" &&
      ("device" in obj || "loop" in obj || "openaps" in obj || "pump" in obj)
    );
  }

  /** Authenticate with API secret */
  authenticate(apiSecret: string): void {
    this.websocketClient.authenticate(apiSecret);
  }

  /** Join specific data rooms for targeted updates */
  joinRoom(room: string): void {
    this.websocketClient.joinRoom(room);
  }

  /** Get connection info */
  getConnectionInfo() {
    return this.websocketClient.getConnectionInfo();
  }

  /** Manual reconnection */
  reconnect(): void {
    this.websocketClient.disconnect();
    setTimeout(() => {
      this.websocketClient.connect();
    }, 1000);
  }

  /** Cleanup */
  destroy(): void {
    this.websocketClient.destroy();
  }
}

/** Creates a realtime store and sets it in context */
export function createRealtimeStore(config: WebSocketConfig): RealtimeStore {
  const store = new RealtimeStore(config);
  setContext(REALTIME_STORE_KEY, store);
  return store;
}

/** Gets the realtime store from context */
export function getRealtimeStore(): RealtimeStore {
  const store = getContext<RealtimeStore>(REALTIME_STORE_KEY);
  if (!store) {
    throw new Error(
      "Realtime store not found. Make sure to call createRealtimeStore in a parent component."
    );
  }
  return store;
}
