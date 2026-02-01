/**
 * Settings Store - Svelte 5 Runes-based store for UI settings
 *
 * This store manages all settings data from the API and provides reactive
 * state that can be shared across settings pages with two-way binding support.
 */

import { getContext, setContext } from "svelte";
import { browser } from "$app/environment";
import { getApiClient } from "$lib/api/client";
import type {
  UISettingsConfiguration,
  DeviceSettings,
  AlgorithmSettings,
  FeatureSettings,
  NotificationSettings,
  ServicesSettings,
} from "$lib/api/api-client";
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

  // xDrip+-style alarm configuration (stored separately for convenience)
  alarmConfiguration = $state<UserAlarmConfiguration>(createDefaultUserAlarmConfiguration());

  // Derived state
  isLoading = $derived(this.loadingState === "loading");
  hasError = $derived(this.loadingState === "error");
  isLoaded = $derived(this.loadingState === "success");

  // Track if we have unsaved changes
  private _hasChanges = $state(false);
  hasUnsavedChanges = $derived(this._hasChanges);

  // Track saving state
  private _isSaving = $state(false);
  isSaving = $derived(this._isSaving);

  constructor() {
    // Auto-load settings when store is created in browser
    if (browser) {
      this.load();
    }
  }

  /**
   * Load settings from the API
   */
  async load(): Promise<void> {
    if (!browser) {
      return;
    }

    // Don't reload if already loading
    if (this.loadingState === "loading") {
      return;
    }

    this.loadingState = "loading";
    this.error = null;

    try {
      const apiClient = getApiClient();
      let settings = await apiClient.uiSettings.getUISettings();

      // Check for locally saved settings that override API response
      const localSettings = localStorage.getItem('nocturne-ui-settings');
      if (localSettings) {
        try {
          const parsed = JSON.parse(localSettings);
          // Merge local settings with API settings (local takes precedence)
          settings = { ...settings, ...parsed };
        } catch {
          // Ignore invalid JSON
        }
      }

      this._rawSettings = settings;

      // Populate individual sections with deep copies for reactivity
      // Note: Therapy settings are managed via Profiles, not here
      this.devices = settings.devices ? { ...settings.devices } : null;
      this.algorithm = settings.algorithm ? { ...settings.algorithm } : null;
      this.features = settings.features ? { ...settings.features } : null;
      this.notifications = settings.notifications ? { ...settings.notifications } : null;
      this.services = settings.services ? { ...settings.services } : null;

      // Load alarm configuration from notifications or create default
      if (settings.notifications?.alarmConfiguration) {
        this.alarmConfiguration = JSON.parse(JSON.stringify(settings.notifications.alarmConfiguration));
      } else {
        this.alarmConfiguration = createDefaultUserAlarmConfiguration();
      }

      this.loadingState = "success";
      this._hasChanges = false;
    } catch (e) {
      this.error = e instanceof Error ? e.message : "Failed to load settings";
      this.loadingState = "error";
    }
  }

  /**
   * Reload settings from the API (force refresh)
   */
  async reload(): Promise<void> {
    this.loadingState = "idle";
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
    // Merge alarm configuration into notifications
    // Note: Using type assertion since our frontend types use string dates while API uses Date
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
      const settings = this.getSettings();
      const apiClient = getApiClient();

      // Save to backend API
      const savedSettings = await apiClient.uiSettings.saveUISettings(settings);

      // Also save to localStorage as fallback/cache
      localStorage.setItem('nocturne-ui-settings', JSON.stringify(settings));

      this._hasChanges = false;
      this._rawSettings = savedSettings;
      return true;
    } catch (e) {
      this.error = e instanceof Error ? e.message : "Failed to save settings";
      // Still try to save to localStorage as fallback
      try {
        const settings = this.getSettings();
        localStorage.setItem('nocturne-ui-settings', JSON.stringify(settings));
      } catch {
        // Ignore localStorage errors
      }
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
      const apiClient = getApiClient();

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

      // Convert to API format (the types are compatible, just use any for the API call)
      const savedConfig = await apiClient.uiSettings.saveAlarmConfiguration(normalizedConfig as any);

      // Update local state with response
      this.alarmConfiguration = savedConfig as unknown as UserAlarmConfiguration;

      // Also update in notifications
      if (this.notifications) {
        this.notifications.alarmConfiguration = savedConfig as NotificationSettings["alarmConfiguration"];
      }

      // Cache in localStorage
      const settings = this.getSettings();
      localStorage.setItem('nocturne-ui-settings', JSON.stringify(settings));

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
        this.error = e instanceof Error ? e.message : "Failed to save alarm configuration";
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
        s => s.id !== id
      );
      this.markChanged();
    }
  }

  syncService(id: string): void {
    if (this.services?.connectedServices) {
      const service = this.services.connectedServices.find(s => s.id === id);
      if (service) {
        service.status = "syncing";
        // Simulate sync completion
        setTimeout(() => {
          if (service) {
            service.status = "connected";
            service.lastSync = new Date();
          }
        }, 2000);
      }
    }
  }
}

/**
 * Creates a settings store and sets it in context
 */
export function createSettingsStore(): SettingsStore {
  const store = new SettingsStore();
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

/**
 * Helper to format last sync time
 */
export function formatLastSync(date: Date | undefined): string {
  if (!date) return "Never";
  const now = Date.now();
  const then = new Date(date).getTime();
  const diff = now - then;
  const minutes = Math.floor(diff / 60000);

  if (minutes < 1) return "Just now";
  if (minutes < 60) return `${minutes}m ago`;

  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;

  return new Date(date).toLocaleDateString();
}
