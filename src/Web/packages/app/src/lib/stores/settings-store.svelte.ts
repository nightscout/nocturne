/**
 * Settings Store - Svelte 5 Runes-based store for UI settings
 *
 * This store manages all settings data from the API and provides reactive
 * state that can be shared across settings pages with two-way binding support.
 */

import { getContext, setContext } from "svelte";
import { browser } from "$app/environment";
import { getApiClient } from "$lib/api/client";
import { getUiSettings } from "$api/ui-settings.remote";
import { describeSubmitError } from "$lib/forms/submit-error";
import { remoteErrorMessage } from "$lib/api/remote-error";
import { SETTINGS_LOAD_FAILED } from "$lib/api/ui-settings-messages";
import type {
  UISettingsConfiguration,
  UserAlarmConfiguration as ApiUserAlarmConfiguration,
  DeviceSettings,
  AlgorithmSettings,
  FeatureSettings,
  NotificationSettings,
  ServicesSettings,
  DataQualitySettings,
  ConnectedService,
} from "$lib/api";
import type { UserAlarmConfiguration } from "$lib/types/alarm-profile";
import {
  createDefaultUserAlarmConfiguration,
  normalizeAlarmPriority,
  normalizeAlarmType,
} from "$lib/types/alarm-profile";

const SETTINGS_STORE_KEY = Symbol("settings-store");

export type SettingsLoadingState = "idle" | "loading" | "success" | "error";

export class SettingsStore {
  // Raw configuration from API
  private _rawSettings = $state<UISettingsConfiguration | null>(null);

  // Loading state
  loadingState = $state<SettingsLoadingState>("idle");
  error = $state<string | null>(null);

  // Individual section states with proper reactivity
  devices = $state<DeviceSettings | null>(null);
  algorithm = $state<AlgorithmSettings | null>(null);
  features = $state<FeatureSettings | null>(null);
  notifications = $state<NotificationSettings | null>(null);
  services = $state<ServicesSettings | null>(null);
  dataQuality = $state<DataQualitySettings | null>(null);

  // xDrip+-style alarm configuration (stored separately for convenience)
  alarmConfiguration = $state<UserAlarmConfiguration>(createDefaultUserAlarmConfiguration());

  // Derived state
  isLoading = $derived(this.loadingState === "loading");
  hasError = $derived(this.loadingState === "error");
  isLoaded = $derived(this.loadingState === "success");

  // Track if we have unsaved changes
  private _hasChanges = $state(false);
  hasUnsavedChanges = $derived(this._hasChanges);

  // `loadingState` cannot stand in for this: the constructor reports "loading"
  // before the read has started.
  private _loadInFlight: Promise<void> | null = null;

  // Track saving state
  private _isSaving = $state(false);
  isSaving = $derived(this._isSaving);

  constructor(autoLoad = true) {
    if (browser && autoLoad) {
      // `.run()` rejects during render, and the store is constructed in the
      // layout's. Reporting the state here rather than leaving it to the
      // deferred load keeps a consumer from rendering against an idle store.
      this.loadingState = "loading";
      queueMicrotask(() => this.load());
    }
  }

  /** Reads the settings, joining a read already in flight. */
  async load(): Promise<void> {
    if (!browser) {
      return;
    }

    this._loadInFlight ??= this.read().finally(() => {
      this._loadInFlight = null;
    });

    await this._loadInFlight;
  }

  private async read(): Promise<void> {
    this.loadingState = "loading";
    this.error = null;

    try {
      // The server is the source of truth. There used to be a localStorage
      // override merged in ahead of the API response, under a key with no user or
      // tenant scoping and nothing to clear it on logout — so on a shared device
      // the second person to sign in inherited the first person's settings, and a
      // save that the server had rejected still read back as persisted.
      const settings = await getUiSettings().run();

      this._rawSettings = settings;

      // Populate individual sections with deep copies for reactivity
      // Note: Therapy settings are managed via Profiles, not here
      this.devices = settings.devices ? { ...settings.devices } : null;
      this.algorithm = settings.algorithm ? { ...settings.algorithm } : null;
      this.features = settings.features ? { ...settings.features } : null;
      this.notifications = settings.notifications ? { ...settings.notifications } : null;
      this.services = settings.services ? { ...settings.services } : null;
      this.dataQuality = settings.dataQuality ? { ...settings.dataQuality } : null;

      // Load alarm configuration from notifications or create default
      if (settings.notifications?.alarmConfiguration) {
        this.alarmConfiguration = JSON.parse(JSON.stringify(settings.notifications.alarmConfiguration));
      } else {
        this.alarmConfiguration = createDefaultUserAlarmConfiguration();
      }

      this.loadingState = "success";
      this._hasChanges = false;
    } catch (e) {
      this.error = remoteErrorMessage(e, SETTINGS_LOAD_FAILED);
      this.loadingState = "error";
    }
  }

  /**
   * Reads the settings again, so a caller that has just written them sees what
   * was stored. A read already in flight was issued before that write, so it is
   * waited out rather than joined.
   */
  async reload(): Promise<void> {
    await this._loadInFlight;
    await this.load();
  }

  /**
   * Mark that changes have been made
   */
  markChanged(): void {
    this._hasChanges = true;
  }

  /**
   * Get combined settings object for saving
   */
  getSettings(): UISettingsConfiguration {
    const notifications = this.notifications ? {
      ...this.notifications,
      alarmConfiguration: this.alarmConfiguration as unknown as NotificationSettings["alarmConfiguration"],
    } : undefined;

    return {
      devices: this.devices ?? undefined,
      algorithm: this.algorithm ?? undefined,
      features: this.features ?? undefined,
      notifications,
      services: this.services ?? undefined,
      dataQuality: this.dataQuality ?? undefined,
    };
  }

  /**
   * Save current settings to the backend API.
   */
  async save(): Promise<boolean> {
    if (!browser) {
      return false;
    }

    this._isSaving = true;
    this.error = null;

    try {
      const savedSettings = await getApiClient().uiSettings.saveUISettings(
        this.getSettings()
      );

      this._hasChanges = false;
      this._rawSettings = savedSettings;
      return true;
    } catch (e) {
      this.error = describeSubmitError(e);
      return false;
    } finally {
      this._isSaving = false;
    }
  }

  /**
   * Save only the alarm configuration to the backend.
   * This is more efficient than saving all settings when only alarms changed.
   */
  async saveAlarmConfiguration(): Promise<boolean> {
    if (!browser) {
      return false;
    }

    this._isSaving = true;
    this.error = null;

    try {
      const profiles = Array.isArray(this.alarmConfiguration?.profiles)
        ? this.alarmConfiguration.profiles
        : [];
      const normalizedProfiles = profiles.map((profile) => ({
        ...profile,
        alarmType: normalizeAlarmType(profile.alarmType),
        priority: normalizeAlarmPriority(profile.priority),
      }));

      const normalizedConfig = {
        ...(this.alarmConfiguration ?? createDefaultUserAlarmConfiguration()),
        profiles: normalizedProfiles,
      };

      const savedConfig = await getApiClient().uiSettings.saveAlarmConfiguration(
        normalizedConfig as unknown as ApiUserAlarmConfiguration
      );

      this.alarmConfiguration = savedConfig as unknown as UserAlarmConfiguration;

      if (this.notifications) {
        this.notifications.alarmConfiguration = savedConfig as NotificationSettings["alarmConfiguration"];
      }

      this._hasChanges = false;
      return true;
    } catch (e) {
      if (e && typeof e === "object" && "errors" in e) {
        const errors = (e as { errors?: Record<string, string[]> }).errors ?? {};
        const messages = Object.entries(errors)
          .flatMap(([key, values]) => values.map((value) => `${key}: ${value}`))
          .filter(Boolean);
        this.error = messages.length > 0 ? messages.join(" | ") : "Validation error";
      } else {
        this.error = describeSubmitError(
          e,
          "We couldn't save your alarm settings. Please try again."
        );
      }
      return false;
    } finally {
      this._isSaving = false;
    }
  }

  /**
   * Reset to original loaded values
   */
  reset(): void {
    if (this._rawSettings) {
      this.devices = this._rawSettings.devices ? { ...this._rawSettings.devices } : null;
      this.algorithm = this._rawSettings.algorithm ? { ...this._rawSettings.algorithm } : null;
      this.features = this._rawSettings.features ? { ...this._rawSettings.features } : null;
      this.notifications = this._rawSettings.notifications ? { ...this._rawSettings.notifications } : null;
      this.services = this._rawSettings.services ? { ...this._rawSettings.services } : null;
      this.dataQuality = this._rawSettings.dataQuality ? { ...this._rawSettings.dataQuality } : null;
      this._hasChanges = false;
    }
  }

  // ==========================================
  // Notification Settings Helpers
  // ==========================================

  addEmergencyContact(): void {
    if (this.notifications?.alarmConfiguration) {
      this.notifications.alarmConfiguration.emergencyContacts = [
        ...(this.notifications.alarmConfiguration.emergencyContacts ?? []),
        {
          id: `contact-${Date.now()}`,
          name: "",
          phone: "",
          criticalOnly: false,
          enabled: true
        }
      ];
      this.markChanged();
    }
  }

  removeEmergencyContact(id: string): void {
    if (this.notifications?.alarmConfiguration?.emergencyContacts) {
      this.notifications.alarmConfiguration.emergencyContacts = this.notifications.alarmConfiguration.emergencyContacts.filter(
        (c: { id?: string }) => c.id !== id
      );
      this.markChanged();
    }
  }

  // ==========================================
  // Services Settings Helpers
  // ==========================================

  removeConnectedService(id: string): void {
    if (this.services?.connectedServices) {
      this.services.connectedServices = this.services.connectedServices.filter(
        (s: ConnectedService) => s.id !== id
      );
      this.markChanged();
    }
  }

}

/**
 * Creates a settings store and sets it in context.
 *
 * @param autoLoad Whether to fetch the settings immediately. Pass false where the settings
 * endpoint cannot answer — it is tenant-scoped, so a host that resolves no tenant would only 404.
 * The store is still placed in context, so every consumer keeps resolving it.
 */
export function createSettingsStore(autoLoad = true): SettingsStore {
  const store = new SettingsStore(autoLoad);
  setContext(SETTINGS_STORE_KEY, store);
  return store;
}

/**
 * Gets the settings store from context
 */
export function getSettingsStore(): SettingsStore {
  const store = getContext<SettingsStore>(SETTINGS_STORE_KEY);
  if (!store) {
    throw new Error(
      "Settings store not found in context. Make sure createSettingsStore() is called in a parent component."
    );
  }
  return store;
}

/**
 * Helper to format time for display (24h -> 12h AM/PM)
 */
export function formatTime(time: string | undefined): string {
  if (!time) return "12:00 AM";
  const [hours, minutes] = time.split(":").map(Number);
  const period = hours >= 12 ? "PM" : "AM";
  const displayHours = hours % 12 || 12;
  return `${displayHours}:${minutes.toString().padStart(2, "0")} ${period}`;
}
