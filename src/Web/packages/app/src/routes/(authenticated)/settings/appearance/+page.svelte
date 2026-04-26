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
  import { updateLanguagePreference } from "$api/user-preferences.remote";
  import {
    getPreference,
    setPreference,
    getSourceDefaults,
    setSourceDefaults,
  } from "$api/generated/glucoseProcessingSettings.generated.remote";
  import GlucoseSourceDefaultsDialog from "$lib/components/settings/GlucoseSourceDefaultsDialog.svelte";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
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
  import { Label as FormLabel } from "$lib/components/ui/label";
  import {
    Activity,
    Palette,
    Sun,
    Moon,
    Monitor,
    Clock,
    Globe,
    Languages,
    AlertCircle,
    Timer,
    Eye,
  } from "lucide-svelte";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";
  import { browser } from "$app/environment";
  import { WidgetId } from "$lib/api/generated/nocturne-api-client";
  import { page } from "$app/state";
  import { coachmark } from "@nocturne/coach";

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

  // Glucose processing settings
  let glucoseProcessingPreference = $state<string | null>(null);
  let sourceDefaults = $state<Array<{ match: string; field: string; processing: string }>>([]);
  let sourceDefaultsDialogOpen = $state(false);

  $effect(() => {
    if (browser) {
      getPreference().then((result) => {
        glucoseProcessingPreference = result?.preferredGlucoseProcessing ?? null;
      });
      getSourceDefaults().then((result) => {
        sourceDefaults = result?.rules ?? [];
      });
    }
  });
</script>

<svelte:head>
  <title>Appearance - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-4xl p-6 space-y-6">
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
            onLanguageChange={page.data.isAuthenticated
              ? (locale: string) => updateLanguagePreference({ preferredLanguage: locale })
              : undefined}
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
    <div {@attach coachmark({
      key: "feature-intro.appearance-widgets",
      title: "Widget order",
      description: "Drag to reorder your dashboard widgets.",
      completeOn: { event: "dragend" },
    })}>
      <DashboardWidgetConfigurator
        value={dashboardTopWidgets.current}
        onchange={handleWidgetsChange}
        maxWidgets={3}
      />
    </div>

    <!-- Chart Options -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Eye class="h-5 w-5" />
          Chart Options
        </CardTitle>
        <CardDescription>Configure chart display preferences</CardDescription>
      </CardHeader>
      <CardContent>
        <div class="grid gap-4 sm:grid-cols-2">
          <div class="space-y-2">
            <FormLabel>Default chart range</FormLabel>
            <Select
              type="single"
              value={String(store.features?.display?.focusHours ?? 3)}
              onValueChange={(value: string) => {
                if (!store.features) return;
                if (!store.features.display) {
                  store.features.display = {};
                }
                store.features.display.focusHours = parseInt(value);
                store.markChanged();
              }}
            >
              <SelectTrigger>
                <span>{store.features?.display?.focusHours ?? 3} hours</span>
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="1">1 hour</SelectItem>
                <SelectItem value="2">2 hours</SelectItem>
                <SelectItem value="3">3 hours</SelectItem>
                <SelectItem value="6">6 hours</SelectItem>
                <SelectItem value="12">12 hours</SelectItem>
                <SelectItem value="24">24 hours</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>
      </CardContent>
    </Card>

    <!-- Glucose Processing -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Activity class="h-5 w-5" />
          Glucose Processing
        </CardTitle>
        <CardDescription>
          Choose how glucose values are displayed when both smoothed and unsmoothed readings are available
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="grid gap-4 sm:grid-cols-2">
          <div class="space-y-2">
            <Label>Display preference</Label>
            <Select
              type="single"
              value={glucoseProcessingPreference ?? "default"}
              onValueChange={async (value) => {
                const newValue = value === "default" ? null : value;
                glucoseProcessingPreference = newValue;
                await setPreference({ preferredGlucoseProcessing: newValue });
              }}
            >
              <SelectTrigger>
                <span>
                  {#if glucoseProcessingPreference === "Smoothed"}
                    Smoothed
                  {:else if glucoseProcessingPreference === "Unsmoothed"}
                    Unsmoothed
                  {:else}
                    Default
                  {/if}
                </span>
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="default">Default</SelectItem>
                <SelectItem value="Smoothed">Smoothed</SelectItem>
                <SelectItem value="Unsmoothed">Unsmoothed</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>
        <p class="text-xs text-muted-foreground">
          This only affects values where both smoothed and unsmoothed readings are present.
          "Default" uses the value as reported by the data source.
        </p>

        <Separator />

        <div class="flex items-center justify-between">
          <div class="space-y-0.5">
            <Label>Client upload source defaults</Label>
            <p class="text-sm text-muted-foreground">
              {#if sourceDefaults.length === 0}
                No source rules configured. All uploads use the default processing.
              {:else}
                {sourceDefaults.length} rule{sourceDefaults.length === 1 ? '' : 's'} configured
              {/if}
            </p>
          </div>
          <Button variant="outline" size="sm" onclick={() => (sourceDefaultsDialogOpen = true)}>
            Configure
          </Button>
        </div>

        <GlucoseSourceDefaultsDialog
          bind:open={sourceDefaultsDialogOpen}
          rules={sourceDefaults}
          onSave={async (rules) => {
            sourceDefaults = rules;
            sourceDefaultsDialogOpen = false;
            await setSourceDefaults({ rules });
          }}
          onCancel={() => (sourceDefaultsDialogOpen = false)}
        />
      </CardContent>
    </Card>

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
