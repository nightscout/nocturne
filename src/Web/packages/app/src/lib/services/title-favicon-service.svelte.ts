/**
 * Title and Favicon Service
 *
 * Manages dynamic browser title and favicon updates based on current glucose values.
 * Integrates with the existing alarm system for flashing behavior.
 */

import { browser } from "$app/environment";
import type { TitleFaviconSettings, ClientThresholds } from "$lib/stores/serverSettings";
import { directions } from "$lib/stores/serverSettings";
import type { AlarmVisualSettings } from "$lib/types/alarm-profile";
import { bg as formatBg, bgDelta as formatBgDelta } from "$lib/utils/formatting";

/** Status levels for glucose values */
export type GlucoseStatus = "very-high" | "high" | "in-range" | "low" | "very-low";

/** Resolve CSS variable to its computed value */
function resolveCssVar(name: string): string {
  if (!browser) return "#000000"; // fallback for SSR
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/** Color palette for different glucose statuses - resolves from CSS variables */
const STATUS_COLORS: Record<GlucoseStatus, () => string> = {
  "very-high": () => resolveCssVar("--glucose-very-high"),
  "high": () => resolveCssVar("--glucose-high"),
  "in-range": () => resolveCssVar("--glucose-in-range"),
  "low": () => resolveCssVar("--glucose-low"),
  "very-low": () => resolveCssVar("--glucose-very-low"),
};

/** Disconnected/stale state color - resolves from CSS variable */
const DISCONNECTED_COLOR = () => resolveCssVar("--muted-foreground");

/** Status labels for alarm display */
const STATUS_LABELS: Record<GlucoseStatus, string> = {
  "very-high": "⚠️ HIGH",
  "high": "HIGH",
  "in-range": "",
  "low": "LOW",
  "very-low": "⚠️ VERY LOW",
};

/**
 * Service for managing dynamic browser title and favicon
 */
export class TitleFaviconService {
  // State
  private initialized = false;
  private canvas: HTMLCanvasElement | null = null;
  private ctx: CanvasRenderingContext2D | null = null;
  private originalFavicon: string = "";
  private linkElement: HTMLLinkElement | null = null;
  private originalTitle: string = "";

  // Flash animation state
  private flashInterval: ReturnType<typeof setInterval> | null = null;
  private isFlashOn = false;
  private currentAlarmVisual: AlarmVisualSettings | null = null;

  // Current values for flashing
  private currentBg = 0;
  private currentStatus: GlucoseStatus = "in-range";
  private currentTitle = "";

  /**
   * Initialize the service - call once on app startup
   */
  initialize(): void {
    if (!browser || this.initialized) return;

    // Store original title
    this.originalTitle = document.title;

    // Create canvas for favicon generation
    this.canvas = document.createElement("canvas");
    this.canvas.width = 32;
    this.canvas.height = 32;
    this.ctx = this.canvas.getContext("2d");

    // Get the existing favicon link element
    this.linkElement = document.querySelector('link[rel="icon"]');
    if (this.linkElement) {
      this.originalFavicon = this.linkElement.href;
    } else {
      // Create one if it doesn't exist
      this.linkElement = document.createElement("link");
      this.linkElement.rel = "icon";
      this.linkElement.type = "image/png";
      document.head.appendChild(this.linkElement);
    }

    this.initialized = true;
  }

  /**
   * Update the browser title and favicon based on current glucose data
   * @param bg - Current blood glucose value
   * @param direction - Trend direction
   * @param delta - Change since last reading
   * @param settings - Title/favicon settings
   * @param thresholds - Glucose thresholds for status calculation
   * @param isDisconnected - Whether the client is disconnected from server
   * @param isStale - Whether the data is stale (old)
   * @param timeSinceReading - Human-readable time since last reading (e.g., "5 min ago")
   */
  update(
    bg: number,
    direction: string,
    delta: number,
    settings: TitleFaviconSettings,
    thresholds: ClientThresholds,
    isDisconnected: boolean = false,
    isStale: boolean = false,
    timeSinceReading: string = ""
  ): void {
    if (!browser || !this.initialized || !settings.enabled) {
      return;
    }

    // Calculate status
    const status = this.getGlucoseStatus(bg, thresholds);

    // Store current values for flashing
    this.currentBg = bg;
    this.currentStatus = status;

    // Update title
    if (settings.showBgValue || settings.showDirection || settings.showDelta) {
      this.currentTitle = this.buildTitle(bg, direction, delta, settings, isDisconnected, isStale, timeSinceReading);
      // Only update if not currently flashing (flash handles its own updates)
      if (!this.flashInterval) {
        document.title = this.currentTitle;
      }
    }

    // Update favicon - use grey when disconnected or stale
    if (settings.faviconEnabled && this.canvas && this.ctx && this.linkElement) {
      let color: string;
      if (isDisconnected || isStale) {
        color = DISCONNECTED_COLOR();
      } else {
        color = settings.faviconColorCoded ? STATUS_COLORS[status]() : resolveCssVar("--muted-foreground");
      }
      const faviconDataUrl = this.generateFaviconDataUrl(
        settings.faviconShowBg ? bg : null,
        color
      );
      // Only update if not currently flashing
      if (!this.flashInterval) {
        this.linkElement.href = faviconDataUrl;
      }
    }
  }

  /**
   * Start flashing animation for alarms
   * Uses AlarmVisualSettings for color and interval
   */
  startFlashing(visualSettings: AlarmVisualSettings): void {
    if (!browser || this.flashInterval) return;

    this.currentAlarmVisual = visualSettings;
    this.isFlashOn = true;

    this.flashInterval = setInterval(() => {
      this.isFlashOn = !this.isFlashOn;
      this.applyFlashState();
    }, visualSettings.flashIntervalMs || 500);

    // Apply initial flash state
    this.applyFlashState();
  }

  /**
   * Stop flashing animation
   */
  stopFlashing(): void {
    if (this.flashInterval) {
      clearInterval(this.flashInterval);
      this.flashInterval = null;
    }
    this.isFlashOn = false;
    this.currentAlarmVisual = null;

    // Restore normal title and favicon
    if (this.currentTitle) {
      document.title = this.currentTitle;
    }
    // Favicon will be updated on next update() call
  }

  /**
   * Apply the current flash state to title and favicon
   */
  private applyFlashState(): void {
    if (!browser) return;

    if (this.isFlashOn) {
      // Flash ON - show warning state
      const statusLabel = STATUS_LABELS[this.currentStatus];
      if (statusLabel) {
        document.title = `${statusLabel} - ${formatBg(this.currentBg)}`;
      }

      // Update favicon with alarm color
      if (this.canvas && this.ctx && this.linkElement && this.currentAlarmVisual) {
        const faviconDataUrl = this.generateFaviconDataUrl(
          this.currentBg,
          this.currentAlarmVisual.flashColor || STATUS_COLORS[this.currentStatus]()
        );
        this.linkElement.href = faviconDataUrl;
      }
    } else {
      // Flash OFF - show normal state or blank
      document.title = this.currentTitle || this.originalTitle;

      // Show original/dim favicon
      if (this.linkElement) {
        if (this.originalFavicon) {
          this.linkElement.href = this.originalFavicon;
        } else if (this.canvas && this.ctx) {
          // Generate dim favicon
          const faviconDataUrl = this.generateFaviconDataUrl(this.currentBg, resolveCssVar("--muted-foreground"));
          this.linkElement.href = faviconDataUrl;
        }
      }
    }
  }

  /**
   * Determine glucose status based on value and thresholds
   */
  getGlucoseStatus(value: number, thresholds: ClientThresholds): GlucoseStatus {
    if (value >= thresholds.high) return "very-high";
    if (value <= thresholds.low) return "very-low";
    if (value > thresholds.targetTop) return "high";
    if (value < thresholds.targetBottom) return "low";
    return "in-range";
  }

  /**
   * Build the browser title string
   */
  private buildTitle(
    bg: number,
    direction: string,
    delta: number,
    settings: TitleFaviconSettings,
    isDisconnected: boolean = false,
    isStale: boolean = false,
    timeSinceReading: string = ""
  ): string {
    const parts: string[] = [];

    // Add prefix if set
    if (settings.customPrefix) {
      parts.push(settings.customPrefix);
    }

    // Show connection error prominently if disconnected
    if (isDisconnected) {
      parts.push("⚠️ Connection Error");
    }

    // Build glucose info part
    const bgParts: string[] = [];

    if (settings.showBgValue) {
      bgParts.push(String(formatBg(bg)));
    }

    if (settings.showDirection && !isDisconnected) {
      const dirInfo = directions[direction as keyof typeof directions];
      if (dirInfo) {
        bgParts.push(dirInfo.label);
      }
    }

    if (settings.showDelta && delta !== undefined && !isDisconnected) {
      bgParts.push(formatBgDelta(delta));
    }

    if (bgParts.length > 0) {
      parts.push(bgParts.join(" "));
    }

    // Add time since reading when stale (but not disconnected - that already shows error)
    if (isStale && !isDisconnected && timeSinceReading) {
      parts.push(`(${timeSinceReading})`);
    }

    return parts.join(" - ");
  }

  /**
   * Generate a favicon data URL using canvas
   */
  private generateFaviconDataUrl(bg: number | null, backgroundColor: string): string {
    if (!this.canvas || !this.ctx) return "";

    const ctx = this.ctx;
    const size = 32;

    // Clear canvas
    ctx.clearRect(0, 0, size, size);

    // Draw rounded rectangle background
    const radius = 6;
    ctx.beginPath();
    ctx.roundRect(0, 0, size, size, radius);
    ctx.fillStyle = backgroundColor;
    ctx.fill();

    // Draw BG value if provided
    if (bg !== null) {
      ctx.fillStyle = resolveCssVar("--foreground");
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";

      // Format BG with user's unit preference
      const bgStr = String(formatBg(bg));

      // Adjust font size based on value length
      if (bgStr.length <= 2) {
        ctx.font = "bold 18px system-ui, sans-serif";
      } else if (bgStr.length <= 3) {
        ctx.font = "bold 14px system-ui, sans-serif";
      } else {
        ctx.font = "bold 11px system-ui, sans-serif";
      }

      ctx.fillText(bgStr, size / 2, size / 2 + 1);
    }

    return this.canvas.toDataURL("image/png");
  }

  /**
   * Reset to original favicon and title
   */
  reset(): void {
    if (!browser) return;

    this.stopFlashing();

    if (this.originalTitle) {
      document.title = this.originalTitle;
    }

    if (this.linkElement && this.originalFavicon) {
      this.linkElement.href = this.originalFavicon;
    }
  }

  /**
   * Cleanup resources
   */
  destroy(): void {
    this.stopFlashing();
    this.canvas = null;
    this.ctx = null;
    this.initialized = false;
  }

  /**
   * Check if flashing is active
   */
  get isFlashing(): boolean {
    return this.flashInterval !== null;
  }
}

// Singleton instance for global access
let instance: TitleFaviconService | null = null;

/**
 * Get the singleton TitleFaviconService instance
 */
export function getTitleFaviconService(): TitleFaviconService {
  if (!instance) {
    instance = new TitleFaviconService();
  }
  return instance;
}
