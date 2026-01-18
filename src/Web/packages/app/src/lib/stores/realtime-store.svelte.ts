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
  TrackerUpdateEvent,
} from "$lib/websocket/types";
import type { DeviceStatus, Profile, TrackerInstanceDto, TrackerDefinitionDto } from "$lib/api";
import { NotificationUrgency } from "$lib/api";
import { toast } from "svelte-sonner";
import { getContext, setContext } from "svelte";
import { getApiClient } from "$lib/api/client";
import { processPillsData, type ProcessedPillsData } from "$lib/data/pills-processor";

const REALTIME_STORE_KEY = Symbol("realtime-store");

// Module-level singleton instance
let singletonStore: RealtimeStore | null = null;

export class RealtimeStore {
  private websocketClient!: WebSocketClient;
  private _initStarted = false;

  /** Loading state - false until initial data is loaded */
  isReady = $state(false);

  /** Current time state (updated every second) */
  now = $state(Date.now());
  private timeInterval: ReturnType<typeof setTimeout> | null = null;

  /** Track when we last received data for backfill purposes */
  private lastDataReceived = Date.now();

  /** Track if sync/backfill is in progress (public for UI feedback) */
  isSyncing = $state(false);

  /** Bound visibility change handler for cleanup */
  private handleVisibilityChange: (() => void) | null = null;

  /** Reactive state using Svelte 5 runes - using $state.raw for arrays to avoid deep proxy issues */
  entries = $state.raw<Entry[]>([]);
  treatments = $state.raw<Treatment[]>([]);
  deviceStatuses = $state.raw<DeviceStatus[]>([]);
  profile = $state<Profile | null>(null);
  trackerInstances = $state.raw<TrackerInstanceDto[]>([]);
  trackerDefinitions = $state.raw<TrackerDefinitionDto[]>([]);

  /** Password reset request counter - increments on each request to trigger refreshes */
  passwordResetRequestCount = $state(0);

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
    return this.now - this.lastUpdated;
  });

  /** Human readable time since last update */
  timeSinceReading = $derived.by(() => {
    const mins = Math.floor(this.timeSinceUpdate / 60000);
    if (mins < 1) return "just now";
    if (mins === 1) return "1 min ago";
    return `${mins} min ago`;
  });

  /** Recent treatments */
  recentTreatments = $derived.by(() => {
    const oneDayAgo = this.now - 24 * 60 * 60 * 1000;
    return this.treatments
      .filter((t) => (t.mills || 0) > oneDayAgo)
      .sort((a, b) => (b.mills || 0) - (a.mills || 0));
  });

  /** Processed pills data (COB, IOB, CAGE, SAGE, Loop, Basal) */
  pillsData = $derived.by((): ProcessedPillsData => {
    // Use hardcoded mg/dL - components handle display formatting themselves
    return processPillsData(
      this.deviceStatuses,
      this.treatments,
      this.profile,
      { units: "mg/dL" }
    );
  });

  /** Active tracker notifications (warn level and above) */
  trackerNotifications = $derived.by(() => {
    return this.trackerInstances
      .map((instance) => {
        const def = this.trackerDefinitions.find((d) => d.id === instance.definitionId);
        if (!def || !def.notificationThresholds) return null;

        // Compute age dynamically from startedAt and current time
        // This ensures notifications update in real-time as time passes
        const age = instance.startedAt
          ? (this.now - new Date(instance.startedAt).getTime()) / (1000 * 60 * 60)
          : instance.ageHours ?? 0;

        if (!age || age <= 0) return null;

        // Determine level from notificationThresholds
        let level: Lowercase<NotificationUrgency> | null = null;

        // Sort thresholds by hours descending to find the highest triggered level
        const sortedThresholds = [...def.notificationThresholds].sort(
          (a, b) => (b.hours ?? 0) - (a.hours ?? 0)
        );

        for (const threshold of sortedThresholds) {
          if (threshold.hours && age >= threshold.hours) {
            const urgency = threshold.urgency;
            if (urgency === NotificationUrgency.Urgent) { level = "urgent"; break; }
            if (urgency === NotificationUrgency.Hazard) { level = "hazard"; break; }
            if (urgency === NotificationUrgency.Warn) { level = "warn"; break; }
            if (urgency === NotificationUrgency.Info) { level = "info"; break; }
          }
        }

        if (!level || level === "info") return null;
        return { ...instance, level, ageHours: age };
      })
      .filter((n): n is TrackerInstanceDto & { level: "warn" | "hazard" | "urgent"; ageHours: number } => n !== null);
  });

  constructor(config: WebSocketConfig) {
    this.websocketClient = new WebSocketClient(config);
    this.setupEventHandlers();
  }

  /** Initialize WebSocket connection - data will be populated via real-time events */
  async initialize(): Promise<void> {
    if (this._initStarted) {
      return;
    }
    this._initStarted = true;

    // Start time ticker and visibility change listener
    if (typeof window !== "undefined") {
      this.timeInterval = setInterval(() => {
        this.now = Date.now();
      }, 1000);

      // Add visibility change listener for sleep/wake detection
      this.handleVisibilityChange = () => {
        if (document.visibilityState === 'visible') {
          // Browser just became visible (wake from sleep, tab switch back)
          console.log('[RealtimeStore] Page became visible, checking for backfill...');
          this.performBackfillIfNeeded();
        }
      };
      document.addEventListener('visibilitychange', this.handleVisibilityChange);
    }

    // Skip if WebSocket URL is not available (SSR scenario)
    if (!this.websocketClient.hasValidUrl()) {
      return;
    }

    try {
      // Fetch historical data using the properly configured API client
      const apiClient = getApiClient();
      const [historicalEntries, historicalTreatments, deviceStatusData, profileData, trackerDefs, trackerActive] = await Promise.all([
        apiClient.entries.getEntries2(undefined, 1000),
        apiClient.treatments.getTreatments2(undefined, 500),
        apiClient.deviceStatus.getDeviceStatus2(undefined, 100).catch(() => []),
        apiClient.profile.getProfiles2(1).catch(() => []),
        apiClient.trackers.getDefinitions().catch(() => []),
        apiClient.trackers.getActiveInstances().catch(() => []),
      ]);

      // Defer all state updates to a microtask to completely break out of the
      // current reactive cycle. This prevents effect_update_depth_exceeded errors
      // when components with PersistedState dependencies are also initializing.
      queueMicrotask(() => {
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

        if (trackerDefs && trackerDefs.length > 0) {
          this.trackerDefinitions = trackerDefs;
        }

        if (trackerActive && trackerActive.length > 0) {
          this.trackerInstances = trackerActive;
        }

        this.isReady = true;
      });
    } catch (error) {
      console.error("Failed to fetch historical data:", error);
      toast.error("Failed to load historical data");
      this.isReady = true; // Still mark as ready to unblock UI
    }

    // Connect to WebSocket bridge
    this.websocketClient.connect();
  }

  /** Setup WebSocket event handlers */
  private setupEventHandlers(): void {
    this.websocketClient.on("connect", () => {
      toast.success("Connected to real-time data");
      // Backfill any missed data on reconnection
      this.performBackfillIfNeeded();
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

    this.websocketClient.on("trackerUpdate", (event: TrackerUpdateEvent) => {
      this.handleTrackerUpdate(event);
    });

    // Admin events - password reset requests
    this.websocketClient.on("passwordResetRequested", () => {
      this.passwordResetRequestCount++;
    });
  }

  /** Handle real-time data updates */
  private handleDataUpdate(event: DataUpdateEvent): void {
    if (!Array.isArray(event.data)) return;

    this.updateLastDataReceived();

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

    this.updateLastDataReceived();

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

  /** Handle tracker updates from SignalR */
  private handleTrackerUpdate(event: TrackerUpdateEvent): void {
    const { action, instance } = event;

    switch (action) {
      case "create":
        // Add new instance if not exists
        if (!this.trackerInstances.some((i) => i.id === instance.id)) {
          this.trackerInstances = [instance, ...this.trackerInstances];
          toast.info(`Tracker started: ${instance.definitionName}`);
        }
        break;

      case "update":
      case "ack":
        // Update existing instance
        const updateIndex = this.trackerInstances.findIndex((i) => i.id === instance.id);
        if (updateIndex !== -1) {
          this.trackerInstances = [
            ...this.trackerInstances.slice(0, updateIndex),
            {
              ...this.trackerInstances[updateIndex],
              ageHours: instance.ageHours,
            },
            ...this.trackerInstances.slice(updateIndex + 1),
          ];
        }
        break;

      case "complete":
      case "delete":
        // Remove from active instances
        this.trackerInstances = this.trackerInstances.filter((i) => i.id !== instance.id);
        if (action === "complete") {
          toast.success(`Tracker completed: ${instance.definitionName}`);
        }
        break;
    }
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

  /** Manual sync - fetch data since last update (exposed for UI trigger) */
  async syncData(): Promise<void> {
    const startTime = Date.now();
    await this.performBackfillIfNeeded(true);

    // Ensure syncing state shows for at least 1 second for visual feedback
    const elapsed = Date.now() - startTime;
    const minDisplayTime = 1000;
    if (elapsed < minDisplayTime) {
      await new Promise(resolve => setTimeout(resolve, minDisplayTime - elapsed));
    }
  }

  /** Cleanup */
  destroy(): void {
    if (this.timeInterval) {
      clearInterval(this.timeInterval);
    }
    if (this.handleVisibilityChange && typeof window !== 'undefined') {
      document.removeEventListener('visibilitychange', this.handleVisibilityChange);
    }
    this.websocketClient.destroy();
  }

  /**
   * Check if backfill is needed and perform it.
   * Called on visibility change (wake from sleep) and WebSocket reconnection.
   * @param force If true, skip the time threshold check (for manual sync)
   */
  private async performBackfillIfNeeded(force = false): Promise<void> {
    // Skip if already syncing
    if (this.isSyncing) {
      return;
    }

    const timeSinceLastData = Date.now() - this.lastDataReceived;
    const fiveMinutes = 5 * 60 * 1000;

    // Only backfill if more than 5 minutes have passed (unless forced)
    if (!force && timeSinceLastData < fiveMinutes) {
      console.log('[RealtimeStore] Data is recent, skipping backfill');
      return;
    }

    this.isSyncing = true;
    const backfillFrom = this.lastDataReceived;

    console.log(
      `[RealtimeStore] Backfilling data from ${new Date(backfillFrom).toISOString()} ` +
      `(${Math.round(timeSinceLastData / 60000)} minutes ago)`
    );

    try {
      const apiClient = getApiClient();

      // Build MongoDB-style find query for entries since lastDataReceived
      const findQuery = JSON.stringify({ mills: { $gte: backfillFrom } });

      // Fetch all data types since last received using existing API methods
      // Note: getDeviceStatus2 doesn't support find queries, so we fetch recent and filter client-side
      const [entries, treatments, deviceStatuses] = await Promise.all([
        apiClient.entries.getEntries2(findQuery, 1000).catch(() => []),
        apiClient.treatments.getTreatments2(findQuery, 500).catch(() => []),
        apiClient.deviceStatus.getDeviceStatus2(100).catch(() => []),
      ]);

      let backfilledCount = 0;

      // Merge entries
      if (entries && entries.length > 0) {
        const newEntries = entries.filter(
          (newEntry: Entry) => !this.entries.some(
            (existing) => existing._id === newEntry._id ||
              (existing.mills === newEntry.mills && existing.sgv === newEntry.sgv)
          )
        );
        if (newEntries.length > 0) {
          this.entries = [...this.entries, ...newEntries]
            .sort((a, b) => (b.mills || 0) - (a.mills || 0))
            .slice(0, 1000);
          backfilledCount += newEntries.length;
        }
      }

      // Merge treatments
      if (treatments && treatments.length > 0) {
        const newTreatments = treatments.filter(
          (newTreatment: Treatment) => !this.treatments.some(
            (existing) => existing._id === newTreatment._id ||
              (existing.mills === newTreatment.mills && existing.eventType === newTreatment.eventType)
          )
        );
        if (newTreatments.length > 0) {
          this.treatments = [...this.treatments, ...newTreatments]
            .sort((a, b) => (b.mills || 0) - (a.mills || 0))
            .slice(0, 500);
          backfilledCount += newTreatments.length;
        }
      }

      // Merge device statuses (filter by timestamp since API doesn't support find query)
      if (deviceStatuses && deviceStatuses.length > 0) {
        const newStatuses = deviceStatuses.filter(
          (newStatus: DeviceStatus) =>
            (newStatus.mills || 0) >= backfillFrom &&
            !this.deviceStatuses.some((existing) => existing._id === newStatus._id)
        );
        if (newStatuses.length > 0) {
          this.deviceStatuses = [...this.deviceStatuses, ...newStatuses]
            .sort((a, b) => (b.mills || 0) - (a.mills || 0))
            .slice(0, 100);
          backfilledCount += newStatuses.length;
        }
      }

      // Update last data received timestamp
      this.lastDataReceived = Date.now();

      if (backfilledCount > 0) {
        console.log(`[RealtimeStore] Backfilled ${backfilledCount} items`);
        toast.success(`Synced ${backfilledCount} missed data points`);
      } else {
        console.log('[RealtimeStore] Backfill complete, no new data');
      }
    } catch (error) {
      console.error('[RealtimeStore] Backfill failed:', error);
      toast.error('Failed to sync missed data');
    } finally {
      this.isSyncing = false;
    }
  }

  /**
   * Update the last data received timestamp.
   * Called when new data arrives via WebSocket.
   */
  private updateLastDataReceived(): void {
    this.lastDataReceived = Date.now();
  }
}

/** Creates a realtime store and sets it in context (singleton - only creates once) */
export function createRealtimeStore(config: WebSocketConfig): RealtimeStore {
  // Return existing singleton if already created
  if (singletonStore) {
    setContext(REALTIME_STORE_KEY, singletonStore);
    return singletonStore;
  }

  const store = new RealtimeStore(config);
  singletonStore = store;
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
