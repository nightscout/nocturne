// Real-time data store using Svelte 5 Runes and WebSocket integration
import { WebSocketClient } from "$lib/websocket/websocket-client.svelte";
import type {
  Entry,
  WebSocketConfig,
  DataUpdateEvent,
  StorageEvent,
  AnnouncementEvent,
  AlarmEvent,
  TrackerUpdateEvent,
  SyncProgressEvent,
} from "$lib/websocket/types";
import type {
  TrackerInstanceDto,
  TrackerDefinitionDto,
  InAppNotificationDto,
  Bolus,
  CarbIntake,
  BGCheck,
  Note,
  DeviceEvent,
  ApsSnapshot,
} from "$lib/api";

/**
 * Nightscout v1/v2 device status shape received via WebSocket and legacy API.
 * The generated client no longer exports this type; define it locally.
 */
export interface DeviceStatus {
  _id?: string;
  mills?: number;
  device?: string;
  loop?: Record<string, any>;
  openaps?: Record<string, any>;
  pump?: Record<string, any>;
  uploader?: Record<string, any>;
  [key: string]: any;
}

/**
 * Nightscout v1/v2 profile shape used for pills (COB, IOB, basal rate, etc.).
 * The generated client no longer exports this type; define it locally.
 */
export interface Profile {
  _id?: string;
  defaultProfile?: string;
  mills?: number;
  startDate?: string;
  units?: string;
  store?: Record<string, any>;
  [key: string]: any;
}
import { NotificationUrgency } from "$lib/api";
import {
  mergeEntryRecords,
  type EntryRecord,
} from "$lib/constants/entry-categories";
import { toast } from "svelte-sonner";
import * as alarmState from "$lib/stores/alarm-state.svelte";
import { getContext, setContext } from "svelte";
import { getApiClient } from "$lib/api/client";
import { processPillsData, type ProcessedPillsData } from "$api/pills-processor";

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

  /** Live sync progress by connector ID (from SignalR sync progress events) */
  syncProgressByConnector = $state<Record<string, SyncProgressEvent>>({});

  /** Bound event handlers for cleanup */
  private handleVisibilityChange: (() => void) | null = null;
  private handleWindowFocus: (() => void) | null = null;

  /** Background polling interval when tab is hidden */
  private backgroundPollInterval: ReturnType<typeof setInterval> | null = null;
  private static readonly BACKGROUND_POLL_MS = 30_000; // 30s — browsers throttle setInterval to ~60s in hidden tabs, so aim for ~1 poll per minute worst-case

  /** Reactive state using Svelte 5 runes - using $state.raw for arrays to avoid deep proxy issues */
  entries = $state.raw<Entry[]>([]);
  deviceStatuses = $state.raw<DeviceStatus[]>([]);
  profile = $state<Profile | null>(null);
  trackerInstances = $state.raw<TrackerInstanceDto[]>([]);
  trackerDefinitions = $state.raw<TrackerDefinitionDto[]>([]);
  inAppNotifications = $state.raw<InAppNotificationDto[]>([]);

  /** V4 record types — used by dashboard and entry components */
  boluses = $state.raw<Bolus[]>([]);
  carbIntakes = $state.raw<CarbIntake[]>([]);
  bgChecks = $state.raw<BGCheck[]>([]);
  notes = $state.raw<Note[]>([]);
  deviceEvents = $state.raw<DeviceEvent[]>([]);
  apsSnapshots = $state.raw<ApsSnapshot[]>([]);

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

  /** Recent v4 entries — merged boluses + carb intakes + bg checks + notes + device events */
  recentEntries = $derived.by((): EntryRecord[] => {
    const oneDayAgo = this.now - 24 * 60 * 60 * 1000;
    return mergeEntryRecords({
      boluses: this.boluses.filter((b) => (b.mills ?? 0) > oneDayAgo),
      carbIntakes: this.carbIntakes.filter((c) => (c.mills ?? 0) > oneDayAgo),
      bgChecks: this.bgChecks.filter((b) => (b.mills ?? 0) > oneDayAgo),
      notes: this.notes.filter((n) => (n.mills ?? 0) > oneDayAgo),
      deviceEvents: this.deviceEvents.filter((d) => (d.mills ?? 0) > oneDayAgo),
    });
  });

  /** Processed pills data (COB, IOB, CAGE, SAGE, Loop, Basal) */
  pillsData = $derived.by((): ProcessedPillsData => {
    // Use hardcoded mg/dL - components handle display formatting themselves
    return processPillsData(
      this.deviceStatuses,
      this.apsSnapshots,
      this.boluses,
      this.carbIntakes,
      this.deviceEvents,
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

      // Add visibility change listener for sleep/wake detection (tab switching)
      this.handleVisibilityChange = () => {
        if (document.visibilityState === 'visible') {
          // Snap now immediately so time-since displays don't lag
          this.now = Date.now();
          this.stopBackgroundPolling();
          console.log('[RealtimeStore] Page became visible, backfilling missed data...');
          // Always backfill on return — timers are unreliable in hidden tabs
          // so we can't trust lastDataReceived to be meaningful
          this.performBackfillIfNeeded(true);
        } else {
          console.log('[RealtimeStore] Page hidden, starting background polling...');
          this.startBackgroundPolling();
        }
      };
      document.addEventListener('visibilitychange', this.handleVisibilityChange);

      // Add window focus listener for app switching (browser stays visible but loses focus)
      this.handleWindowFocus = () => {
        this.now = Date.now();
        console.log('[RealtimeStore] Window focused, checking for backfill...');
        this.performBackfillIfNeeded();
      };
      window.addEventListener('focus', this.handleWindowFocus);
    }

    // Skip if WebSocket URL is not available (SSR scenario)
    if (!this.websocketClient.hasValidUrl()) {
      return;
    }

    try {
      // Fetch historical data using the properly configured API client
      const apiClient = getApiClient();
      const oneDayAgo = new Date(Date.now() - 24 * 60 * 60 * 1000);
      const now = new Date();
      const [
        historicalEntries,
        deviceStatusData,
        profileData,
        trackerDefs,
        trackerActive,
        notifications,
        historicalBoluses,
        historicalCarbIntakes,
        historicalBgChecks,
        historicalNotes,
        historicalDeviceEvents,
        historicalApsSnapshots,
      ] = await Promise.all([
        apiClient.sensorGlucose.getAll(undefined, undefined, 1000).then((r) => (r.data ?? []) as unknown as Entry[]).catch(() => [] as Entry[]),
        Promise.resolve([] as DeviceStatus[]),
        apiClient.profile.getProfileSummary().catch(() => null),
        apiClient.trackers.getDefinitions().catch(() => []),
        apiClient.trackers.getActiveInstances().catch(() => []),
        apiClient.notifications.getNotifications().catch(() => []),
        apiClient.bolus.getAll(oneDayAgo, now, 500).then((r) => r.data ?? []).catch((e) => { console.error("Failed to load boluses:", e); return []; }),
        apiClient.nutrition.getCarbIntakes(oneDayAgo, now, 500).then((r) => r.data ?? []).catch((e) => { console.error("Failed to load carbIntakes:", e); return []; }),
        apiClient.bGCheck.getAll(oneDayAgo, now, 500).then((r) => r.data ?? []).catch((e) => { console.error("Failed to load bgChecks:", e); return []; }),
        apiClient.note.getAll(oneDayAgo, now, 500).then((r) => r.data ?? []).catch((e) => { console.error("Failed to load notes:", e); return []; }),
        apiClient.deviceEvent.getAll(oneDayAgo, now, 500).then((r) => r.data ?? []).catch((e) => { console.error("Failed to load deviceEvents:", e); return []; }),
        apiClient.apsSnapshot.getAll(oneDayAgo, now, 50).then((r) => r.data ?? []).catch((e) => { console.error("Failed to load apsSnapshots:", e); return []; }),
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

        if (deviceStatusData && deviceStatusData.length > 0) {
          this.deviceStatuses = deviceStatusData.sort(
            (a: DeviceStatus, b: DeviceStatus) => (b.mills || 0) - (a.mills || 0)
          );
        }

        if (profileData) {
          this.profile = profileData as unknown as Profile;
        }

        if (trackerDefs && trackerDefs.length > 0) {
          this.trackerDefinitions = trackerDefs;
        }

        if (trackerActive && trackerActive.length > 0) {
          this.trackerInstances = trackerActive;
        }

        if (notifications && notifications.length > 0) {
          this.inAppNotifications = notifications;
        }

        // Populate v4 record arrays
        if (historicalBoluses && historicalBoluses.length > 0) {
          this.boluses = historicalBoluses.sort(
            (a: Bolus, b: Bolus) => (b.mills || 0) - (a.mills || 0)
          );
        }
        if (historicalCarbIntakes && historicalCarbIntakes.length > 0) {
          this.carbIntakes = historicalCarbIntakes.sort(
            (a: CarbIntake, b: CarbIntake) => (b.mills || 0) - (a.mills || 0)
          );
        }
        if (historicalBgChecks && historicalBgChecks.length > 0) {
          this.bgChecks = historicalBgChecks.sort(
            (a: BGCheck, b: BGCheck) => (b.mills || 0) - (a.mills || 0)
          );
        }
        if (historicalNotes && historicalNotes.length > 0) {
          this.notes = historicalNotes.sort(
            (a: Note, b: Note) => (b.mills || 0) - (a.mills || 0)
          );
        }
        if (historicalDeviceEvents && historicalDeviceEvents.length > 0) {
          this.deviceEvents = historicalDeviceEvents.sort(
            (a: DeviceEvent, b: DeviceEvent) => (b.mills || 0) - (a.mills || 0)
          );
        }

        if (historicalApsSnapshots && historicalApsSnapshots.length > 0) {
          this.apsSnapshots = historicalApsSnapshots.sort(
            (a: ApsSnapshot, b: ApsSnapshot) => (b.mills || 0) - (a.mills || 0)
          );
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

    // In-app notification events
    this.websocketClient.on("notificationCreated", (notification: InAppNotificationDto) => {
      this.handleNotificationCreated(notification);
    });

    this.websocketClient.on("notificationArchived", (notification: InAppNotificationDto) => {
      this.handleNotificationArchived(notification);
    });

    this.websocketClient.on("notificationUpdated", (notification: InAppNotificationDto) => {
      this.handleNotificationUpdated(notification);
    });

    this.websocketClient.on("syncProgress", (event: SyncProgressEvent) => {
      if (event.phase === "Syncing") {
        this.syncProgressByConnector = { ...this.syncProgressByConnector, [event.connectorId]: event };
      } else {
        // Show completed/failed state briefly, then clear
        this.syncProgressByConnector = { ...this.syncProgressByConnector, [event.connectorId]: event };
        setTimeout(() => {
          const { [event.connectorId]: _, ...rest } = this.syncProgressByConnector;
          this.syncProgressByConnector = rest;
        }, 2000);
      }
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
    } else if (colName === "devicestatus" && this.isDeviceStatus(doc)) {
      const exists = this.deviceStatuses.some(
        (ds) => ds._id === doc._id
      );

      if (!exists) {
        this.deviceStatuses = [doc, ...this.deviceStatuses]
          .sort((a, b) => (b.mills || 0) - (a.mills || 0))
          .slice(0, 100);
      }

      // The backend decomposes devicestatus into an ApsSnapshot asynchronously.
      // Wait briefly then fetch the latest snapshot so pills update in real time.
      setTimeout(() => this.refreshLatestApsSnapshot(), 3000);
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
    }
  }

  /** Handle storage delete events */
  private handleDelete(event: StorageEvent): void {
    const { colName, doc } = event;

    if (colName === "entries") {
      this.entries = this.entries.filter((entry) => entry._id !== doc._id);
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

    // Trigger full-screen alarm overlay and sound
    alarmState.trigger(event);

    // Keep toast as secondary notification
    toast[toastMethod](event.message || "Glucose alarm", {
      description: event.title,
      duration: isUrgent ? 10000 : 5000,
    });
  }

  /** Handle alarm clearing */
  private handleClearAlarm(): void {
    // Clear alarm overlay, sound, and snooze state
    alarmState.clear();

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

  /** Handle new in-app notification from SignalR */
  private handleNotificationCreated(notification: InAppNotificationDto): void {
    // Add if not already present
    if (!this.inAppNotifications.some((n) => n.id === notification.id)) {
      this.inAppNotifications = [notification, ...this.inAppNotifications];
    }
  }

  /** Handle notification archived from SignalR */
  private handleNotificationArchived(notification: InAppNotificationDto): void {
    // Remove from active notifications
    this.inAppNotifications = this.inAppNotifications.filter((n) => n.id !== notification.id);
  }

  /** Handle notification updated from SignalR */
  private handleNotificationUpdated(notification: InAppNotificationDto): void {
    const index = this.inAppNotifications.findIndex((n) => n.id === notification.id);
    if (index !== -1) {
      this.inAppNotifications = [
        ...this.inAppNotifications.slice(0, index),
        notification,
        ...this.inAppNotifications.slice(index + 1),
      ];
    } else {
      // If not found, treat as create
      this.handleNotificationCreated(notification);
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

  private isDeviceStatus(obj: any): obj is DeviceStatus {
    return (
      obj &&
      typeof obj === "object" &&
      ("device" in obj || "loop" in obj || "openaps" in obj || "pump" in obj)
    );
  }

  /**
   * Find a v4 entry record by the treatmentId used in chart markers.
   * Chart markers use `LegacyId ?? Id.ToString()` as treatmentId,
   * so we match against both `legacyId` and `id` on v4 records.
   */
  findEntryByTreatmentId(treatmentId: string): EntryRecord | undefined {
    const bolus = this.boluses.find(
      (b) => b.id === treatmentId || b.legacyId === treatmentId,
    );
    if (bolus) return { kind: "bolus", data: bolus };

    const carb = this.carbIntakes.find(
      (c) => c.id === treatmentId || c.legacyId === treatmentId,
    );
    if (carb) return { kind: "carbs", data: carb };

    const bg = this.bgChecks.find(
      (b) => b.id === treatmentId || b.legacyId === treatmentId,
    );
    if (bg) return { kind: "bgCheck", data: bg };

    const note = this.notes.find(
      (n) => n.id === treatmentId || n.legacyId === treatmentId,
    );
    if (note) return { kind: "note", data: note };

    const de = this.deviceEvents.find(
      (d) => d.id === treatmentId || d.legacyId === treatmentId,
    );
    if (de) return { kind: "deviceEvent", data: de };

    return undefined;
  }

  /**
   * Find all v4 entry records correlated with a given record via correlationId.
   * Excludes the record itself.
   */
  findCorrelatedEntries(record: EntryRecord): EntryRecord[] {
    const corrId = record.data.correlationId;
    if (!corrId) return [];

    const results: EntryRecord[] = [];
    for (const b of this.boluses) {
      if (b.correlationId === corrId && b.id !== record.data.id)
        results.push({ kind: "bolus", data: b });
    }
    for (const c of this.carbIntakes) {
      if (c.correlationId === corrId && c.id !== record.data.id)
        results.push({ kind: "carbs", data: c });
    }
    for (const bg of this.bgChecks) {
      if (bg.correlationId === corrId && bg.id !== record.data.id)
        results.push({ kind: "bgCheck", data: bg });
    }
    for (const n of this.notes) {
      if (n.correlationId === corrId && n.id !== record.data.id)
        results.push({ kind: "note", data: n });
    }
    for (const de of this.deviceEvents) {
      if (de.correlationId === corrId && de.id !== record.data.id)
        results.push({ kind: "deviceEvent", data: de });
    }
    return results;
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
    this.stopBackgroundPolling();
    if (typeof window !== 'undefined') {
      if (this.handleVisibilityChange) {
        document.removeEventListener('visibilitychange', this.handleVisibilityChange);
      }
      if (this.handleWindowFocus) {
        window.removeEventListener('focus', this.handleWindowFocus);
      }
    }
    this.websocketClient.destroy();
  }

  /**
   * Start polling for data while the tab is hidden.
   * Browsers throttle setInterval in background tabs (Chrome: ~60s minimum),
   * so we set a 30s interval knowing it'll fire roughly once per minute.
   * This ensures glucose values stay current even when the tab isn't focused.
   */
  private startBackgroundPolling(): void {
    if (this.backgroundPollInterval) return;

    this.backgroundPollInterval = setInterval(() => {
      this.performBackfillIfNeeded(true);
    }, RealtimeStore.BACKGROUND_POLL_MS);
  }

  /** Stop background polling (called when tab becomes visible again) */
  private stopBackgroundPolling(): void {
    if (this.backgroundPollInterval) {
      clearInterval(this.backgroundPollInterval);
      this.backgroundPollInterval = null;
    }
  }

  /** Fetch the most recent APS snapshots and merge any new ones into the store. */
  private async refreshLatestApsSnapshot(): Promise<void> {
    try {
      const apiClient = getApiClient();
      const fiveMinAgo = new Date(Date.now() - 5 * 60 * 1000);
      const result = await apiClient.apsSnapshot.getAll(fiveMinAgo, new Date(), 5);
      const snapshots = result.data ?? [];
      if (snapshots.length === 0) return;
      const added = snapshots.filter(
        (s: ApsSnapshot) => !this.apsSnapshots.some((existing) => existing.id === s.id)
      );
      if (added.length > 0) {
        this.apsSnapshots = [...added, ...this.apsSnapshots]
          .sort((a, b) => (b.mills || 0) - (a.mills || 0))
          .slice(0, 50);
      }
    } catch {
      // Non-critical — pills will update on next backfill
    }
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

      // Fetch all data types since last received using existing API methods
      const backfillFromDate = new Date(backfillFrom);
      const nowDate = new Date();
      const [entries, deviceStatuses, boluses, carbIntakes, bgChecks, notes, devEvents, newApsSnapshots] = await Promise.all([
        apiClient.sensorGlucose.getAll(backfillFromDate, nowDate, 1000).then((r) => (r.data ?? []) as unknown as Entry[]).catch(() => [] as Entry[]),
        Promise.resolve([] as DeviceStatus[]),
        apiClient.bolus.getAll(backfillFromDate, nowDate, 500).then((r) => r.data ?? []).catch(() => []),
        apiClient.nutrition.getCarbIntakes(backfillFromDate, nowDate, 500).then((r) => r.data ?? []).catch(() => []),
        apiClient.bGCheck.getAll(backfillFromDate, nowDate, 500).then((r) => r.data ?? []).catch(() => []),
        apiClient.note.getAll(backfillFromDate, nowDate, 500).then((r) => r.data ?? []).catch(() => []),
        apiClient.deviceEvent.getAll(backfillFromDate, nowDate, 500).then((r) => r.data ?? []).catch(() => []),
        apiClient.apsSnapshot.getAll(backfillFromDate, nowDate, 20).then((r) => r.data ?? []).catch(() => []),
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

      // Merge v4 records
      if (boluses && boluses.length > 0) {
        const newBoluses = boluses.filter(
          (b: Bolus) => !this.boluses.some((existing) => existing.id === b.id)
        );
        if (newBoluses.length > 0) {
          this.boluses = [...this.boluses, ...newBoluses]
            .sort((a, b) => (b.mills || 0) - (a.mills || 0))
            .slice(0, 500);
          backfilledCount += newBoluses.length;
        }
      }
      if (carbIntakes && carbIntakes.length > 0) {
        const newCarbs = carbIntakes.filter(
          (c: CarbIntake) => !this.carbIntakes.some((existing) => existing.id === c.id)
        );
        if (newCarbs.length > 0) {
          this.carbIntakes = [...this.carbIntakes, ...newCarbs]
            .sort((a, b) => (b.mills || 0) - (a.mills || 0))
            .slice(0, 500);
          backfilledCount += newCarbs.length;
        }
      }
      if (bgChecks && bgChecks.length > 0) {
        const newBg = bgChecks.filter(
          (b: BGCheck) => !this.bgChecks.some((existing) => existing.id === b.id)
        );
        if (newBg.length > 0) {
          this.bgChecks = [...this.bgChecks, ...newBg]
            .sort((a, b) => (b.mills || 0) - (a.mills || 0))
            .slice(0, 500);
          backfilledCount += newBg.length;
        }
      }
      if (notes && notes.length > 0) {
        const newNotes = notes.filter(
          (n: Note) => !this.notes.some((existing) => existing.id === n.id)
        );
        if (newNotes.length > 0) {
          this.notes = [...this.notes, ...newNotes]
            .sort((a, b) => (b.mills || 0) - (a.mills || 0))
            .slice(0, 500);
          backfilledCount += newNotes.length;
        }
      }
      if (devEvents && devEvents.length > 0) {
        const newDevEvents = devEvents.filter(
          (d: DeviceEvent) => !this.deviceEvents.some((existing) => existing.id === d.id)
        );
        if (newDevEvents.length > 0) {
          this.deviceEvents = [...this.deviceEvents, ...newDevEvents]
            .sort((a, b) => (b.mills || 0) - (a.mills || 0))
            .slice(0, 500);
          backfilledCount += newDevEvents.length;
        }
      }

      if (newApsSnapshots && newApsSnapshots.length > 0) {
        const addedSnapshots = newApsSnapshots.filter(
          (s: ApsSnapshot) => !this.apsSnapshots.some((existing) => existing.id === s.id)
        );
        if (addedSnapshots.length > 0) {
          this.apsSnapshots = [...this.apsSnapshots, ...addedSnapshots]
            .sort((a, b) => (b.mills || 0) - (a.mills || 0))
            .slice(0, 50);
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

/** Gets the realtime store from context, returning null if not available */
export function tryGetRealtimeStore(): RealtimeStore | null {
  return getContext<RealtimeStore>(REALTIME_STORE_KEY) ?? null;
}
