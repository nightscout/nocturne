<script lang="ts">
  import { getSettingsStore } from "$lib/stores/settings-store.svelte";
  import {
    getColorTheme,
    setColorTheme,
    type ColorTheme,
  } from "$lib/stores/appearance-store.svelte";
  import {
    glucoseUnits,
    timeFormat,
    nightModeSchedule,
    setColorScheme,
    userPrefersMode,
    dashboardTopWidgets,
    type ColorScheme,
  } from "$lib/stores/appearance-store.svelte";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import TitleFaviconSettings from "$lib/components/settings/TitleFaviconSettings.svelte";
  import DashboardWidgetConfigurator from "$lib/components/settings/DashboardWidgetConfigurator.svelte";
  import LanguageSelector from "$lib/components/LanguageSelector.svelte";
  import { updateLanguagePreference } from "$lib/data/user-preferences.remote";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
  } from "$lib/components/ui/select";
  import {
    Palette,
    Sun,
    Moon,
    Monitor,
    Clock,
    Globe,
    Languages,
    AlertCircle,
    Timer,
  } from "lucide-svelte";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";
  import { browser } from "$app/environment";
  import { WidgetId } from "$lib/api/generated/nocturne-api-client";
  import { page } from "$app/state";

  const store = getSettingsStore();
  const realtimeStore = getRealtimeStore();

  // Dashboard widgets - use persisted state for immediate localStorage persistence
  function handleWidgetsChange(widgets: WidgetId[]) {
    dashboardTopWidgets.current = widgets;
  }

  // Theme state - reactive wrapper around store (color theme: nocturne/trio)
  let currentTheme = $state<ColorTheme>(getColorTheme());

  // Handle color theme change (Nocturne vs Trio) with runtime switching
  function handleThemeChange(theme: ColorTheme) {
    if (currentTheme === theme) return;
    setColorTheme(theme);
    currentTheme = theme;
  }

  // Reactive derived value for current color scheme (light/dark/system)
  const currentColorScheme = $derived<ColorScheme>(
    userPrefersMode.current ?? "system"
  );

  // Get browser timezone
  const browserTimezone = $derived.by(() => {
    if (!browser) return "Unknown";
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone;
    } catch {
      return "Unknown";
    }
  });

  // Get timezone offset
  const timezoneOffset = $derived.by(() => {
    if (!browser) return "";
    try {
      const offset = new Date().getTimezoneOffset();
      const hours = Math.abs(Math.floor(offset / 60));
      const minutes = Math.abs(offset % 60);
      const sign = offset <= 0 ? "+" : "-";
      return `UTC${sign}${hours.toString().padStart(2, "0")}:${minutes.toString().padStart(2, "0")}`;
    } catch {
      return "";
    }
  });

  // Current time in timezone for display
  const currentTime = $derived(
    new Date(realtimeStore.now).toLocaleTimeString(undefined, {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    })
  );
</script>

<svelte:head>
  <title>Appearance - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-3xl space-y-6">
  <!-- Header -->
  <div>
    <h1 class="text-2xl font-bold tracking-tight">Appearance</h1>
    <p class="text-muted-foreground">Customize the look and feel of Nocturne</p>
  </div>

  {#if store.isLoading}
    <SettingsPageSkeleton cardCount={4} />
  {:else if store.hasError}
    <Card class="border-destructive">
      <CardContent class="flex items-center gap-3 py-6">
        <AlertCircle class="h-5 w-5 text-destructive" />
        <div>
          <p class="font-medium">Failed to load settings</p>
          <p class="text-sm text-muted-foreground">{store.error}</p>
        </div>
      </CardContent>
    </Card>
  {:else}
    <!-- Theme Selection -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Palette class="h-5 w-5" />
          Color Theme
        </CardTitle>
        <CardDescription>
          Choose between Nocturne's custom theme or match the Trio iOS app
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="grid gap-4 sm:grid-cols-2">
          <!-- Nocturne Theme -->
          <button
            type="button"
            class="relative flex flex-col items-start gap-2 rounded-lg border-2 p-4 text-left transition-colors hover:bg-accent/50 {currentTheme ===
            'nocturne'
              ? 'border-primary bg-accent/30'
              : 'border-border'}"
            onclick={() => {
              if (currentTheme !== "nocturne") {
                handleThemeChange("nocturne");
              }
            }}
          >
            {#if currentTheme === "nocturne"}
              <Badge class="absolute right-2 top-2" variant="default">
                Active
              </Badge>
            {/if}
            <div class="font-semibold">Nocturne</div>
            <p class="text-sm text-muted-foreground">
              Custom color palette designed for Nocturne
            </p>
            <!-- Color preview -->
            <div class="flex gap-1 mt-2">
              <div
                class="h-4 w-4 rounded-full"
                style="background: oklch(0.6 0.118 184.704)"
                title="In Range"
              ></div>
              <div
                class="h-4 w-4 rounded-full"
                style="background: oklch(0.646 0.222 41.116)"
                title="Low"
              ></div>
              <div
                class="h-4 w-4 rounded-full"
                style="background: oklch(0.577 0.245 27.325)"
                title="Very Low"
              ></div>
              <div
                class="h-4 w-4 rounded-full"
                style="background: #7928ca"
                title="High"
              ></div>
              <div
                class="h-4 w-4 rounded-full"
                style="background: oklch(0.769 0.188 70.08)"
                title="Carbs"
              ></div>
            </div>
          </button>

          <!-- Trio Theme -->
          <button
            type="button"
            class="relative flex flex-col items-start gap-2 rounded-lg border-2 p-4 text-left transition-colors hover:bg-accent/50 {currentTheme ===
            'trio'
              ? 'border-primary bg-accent/30'
              : 'border-border'}"
            onclick={() => {
              if (currentTheme !== "trio") {
                handleThemeChange("trio");
              }
            }}
          >
            {#if currentTheme === "trio"}
              <Badge class="absolute right-2 top-2" variant="default">
                Active
              </Badge>
            {/if}
            <div class="font-semibold">Trio</div>
            <p class="text-sm text-muted-foreground">
              Match the Trio iOS app color scheme
            </p>
            <!-- Color preview -->
            <div class="flex gap-1 mt-2">
              <div
                class="h-4 w-4 rounded-full"
                style="background: rgb(111, 207, 151)"
                title="LoopGreen"
              ></div>
              <div
                class="h-4 w-4 rounded-full"
                style="background: rgb(255, 193, 69)"
                title="LoopYellow"
              ></div>
              <div
                class="h-4 w-4 rounded-full"
                style="background: rgb(235, 87, 87)"
                title="LoopRed"
              ></div>
              <div
                class="h-4 w-4 rounded-full"
                style="background: rgb(30, 150, 252)"
                title="Insulin"
              ></div>
              <div
                class="h-4 w-4 rounded-full"
                style="background: rgb(255, 240, 23)"
                title="Carbs"
              ></div>
            </div>
          </button>
        </div>

        <p class="text-xs text-muted-foreground">
          Theme changes take effect immediately
        </p>
      </CardContent>
    </Card>

    <!-- Color Scheme (Dark/Light Mode) -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Sun class="h-5 w-5" />
          Color Scheme
        </CardTitle>
        <CardDescription>Choose between light and dark mode</CardDescription>
      </CardHeader>
      <CardContent class="space-y-6">
        <div class="grid gap-4 sm:grid-cols-2">
          <div class="space-y-2">
            <Label>Mode</Label>
            <Select
              type="single"
              value={currentColorScheme}
              onValueChange={(value) => {
                setColorScheme(value as ColorScheme);
              }}
            >
              <SelectTrigger>
                <span class="flex items-center gap-2">
                  {#if currentColorScheme === "light"}
                    <Sun class="h-4 w-4" />
                    Light
                  {:else if currentColorScheme === "dark"}
                    <Moon class="h-4 w-4" />
                    Dark
                  {:else}
                    <Monitor class="h-4 w-4" />
                    System
                  {/if}
                </span>
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="system">
                  <span class="flex items-center gap-2">
                    <Monitor class="h-4 w-4" />
                    System
                  </span>
                </SelectItem>
                <SelectItem value="light">
                  <span class="flex items-center gap-2">
                    <Sun class="h-4 w-4" />
                    Light
                  </span>
                </SelectItem>
                <SelectItem value="dark">
                  <span class="flex items-center gap-2">
                    <Moon class="h-4 w-4" />
                    Dark
                  </span>
                </SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>

        <Separator />

        <div class="flex items-center justify-between">
          <div class="space-y-0.5">
            <Label>Night mode schedule</Label>
            <p class="text-sm text-muted-foreground">
              Automatically switch to dark theme at night
            </p>
          </div>
          <Switch
            checked={nightModeSchedule.current}
            onCheckedChange={(checked) => {
              nightModeSchedule.current = checked;
            }}
          />
        </div>
      </CardContent>
    </Card>

    <!-- Language Selection -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Languages class="h-5 w-5" />
          Language
        </CardTitle>
        <CardDescription>
          Choose your preferred language for the interface
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="space-y-2">
          <Label>Display language</Label>
          <LanguageSelector
            onLanguageChange={page.data.isAuthenticated ? updateLanguagePreference : undefined}
          />
        </div>
        <p class="text-xs text-muted-foreground">
          {#if page.data.isAuthenticated}
            Your language preference will be saved to your account and synced across devices.
          {:else}
            Sign in to sync your language preference across devices.
          {/if}
        </p>
      </CardContent>
    </Card>

    <!-- Dashboard Widgets -->
    <DashboardWidgetConfigurator
      value={dashboardTopWidgets.current}
      onchange={handleWidgetsChange}
      maxWidgets={3}
    />

    <!-- Tracker Pills -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Timer class="h-5 w-5" />
          Tracker Pills
        </CardTitle>
        <CardDescription>
          Show active tracker ages on the dashboard
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-6">
        <div class="flex items-center justify-between">
          <div class="space-y-0.5">
            <Label>Show tracker pills</Label>
            <p class="text-sm text-muted-foreground">
              Display active tracker ages (sensor, pump site, etc.) on homepage
            </p>
          </div>
          <Switch
            checked={store.features?.trackerPills?.enabled ?? true}
            onCheckedChange={(checked) => {
              if (!store.features) return;
              if (!store.features.trackerPills) {
                store.features.trackerPills = {
                  enabled: true,
                };
              }
              store.features.trackerPills.enabled = checked;
              store.markChanged();
            }}
          />
        </div>

        <p class="text-xs text-muted-foreground">
          Each tracker's dashboard visibility is configured in
          <a href="/settings/trackers" class="text-primary hover:underline">
            Settings → Trackers
          </a>
        </p>
      </CardContent>
    </Card>

    <!-- Units & Formats -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Globe class="h-5 w-5" />
          Units & Formats
        </CardTitle>
        <CardDescription>
          Configure measurement units and display formats
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="grid gap-4 sm:grid-cols-2">
          <div class="space-y-2">
            <Label>Blood glucose units</Label>
            <Select
              type="single"
              value={glucoseUnits.current}
              onValueChange={(value) => {
                glucoseUnits.current = value as "mg/dl" | "mmol";
              }}
            >
              <SelectTrigger>
                <span>
                  {glucoseUnits.current === "mg/dl" ? "mg/dL" : "mmol/L"}
                </span>
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="mg/dl">mg/dL</SelectItem>
                <SelectItem value="mmol">mmol/L</SelectItem>
              </SelectContent>
            </Select>
          </div>

          <div class="space-y-2">
            <Label>Time format</Label>
            <Select
              type="single"
              value={timeFormat.current}
              onValueChange={(value) => {
                timeFormat.current = value as "12" | "24";
              }}
            >
              <SelectTrigger>
                <span>
                  {timeFormat.current === "12" ? "12-hour (AM/PM)" : "24-hour"}
                </span>
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="12">12-hour (AM/PM)</SelectItem>
                <SelectItem value="24">24-hour</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>
      </CardContent>
    </Card>

    <!-- Timezone -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Clock class="h-5 w-5" />
          Timezone
        </CardTitle>
        <CardDescription>
          Your device's current timezone settings
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div class="grid gap-4 sm:grid-cols-2">
          <div class="space-y-1">
            <Label class="text-muted-foreground text-xs">Timezone</Label>
            <p class="font-medium">{browserTimezone}</p>
          </div>
          <div class="space-y-1">
            <Label class="text-muted-foreground text-xs">UTC Offset</Label>
            <p class="font-medium">{timezoneOffset}</p>
          </div>
          <div class="space-y-1">
            <Label class="text-muted-foreground text-xs">Current Time</Label>
            <p class="font-medium font-mono">{currentTime}</p>
          </div>
        </div>
        <p class="text-xs text-muted-foreground mt-4">
          Timezone is automatically detected from your device. Data is displayed
          in this timezone.
        </p>
      </CardContent>
    </Card>

    <!-- Browser Tab Settings (Favicon) -->
    <TitleFaviconSettings />
  {/if}
</div>
