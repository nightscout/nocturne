<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Input } from "$lib/components/ui/input";
  import { Separator } from "$lib/components/ui/separator";
  import { Globe, Bell } from "lucide-svelte";
  import { browser } from "$app/environment";
  import type { TitleFaviconSettings } from "$lib/stores/serverSettings";
  import { getDefaultSettings } from "$lib/components/settings/constants";

  // Local storage key
  const STORAGE_KEY = "nocturne-title-favicon-settings";

  // Get default settings
  const defaults = getDefaultSettings().titleFavicon;

  // Load settings from localStorage
  function loadSettings(): TitleFaviconSettings {
    if (!browser) return defaults;
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      if (stored) {
        return { ...defaults, ...JSON.parse(stored) };
      }
    } catch (e) {
      console.error("Failed to load title/favicon settings:", e);
    }
    return defaults;
  }

  // Save settings to localStorage
  function saveSettings(settings: TitleFaviconSettings) {
    if (!browser) return;
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
    } catch (e) {
      console.error("Failed to save title/favicon settings:", e);
    }
  }

  // Reactive settings state
  let settings = $state<TitleFaviconSettings>(loadSettings());

  // Save whenever settings change
  $effect(() => {
    saveSettings(settings);
  });
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <Globe class="h-5 w-5" />
      Browser Tab Settings
    </CardTitle>
    <CardDescription>
      Customize how glucose data appears in the browser tab
    </CardDescription>
  </CardHeader>
  <CardContent class="space-y-6 @container">
    <!-- Master Enable -->
    <div class="flex items-center justify-between">
      <div class="space-y-0.5">
        <Label>Enable dynamic title & favicon</Label>
        <p class="text-sm text-muted-foreground">
          Show glucose values in browser tab
        </p>
      </div>
      <Switch
        checked={settings.enabled}
        onCheckedChange={(checked: boolean) => {
          settings.enabled = checked;
        }}
      />
    </div>

    {#if settings.enabled}
      <Separator />

      <!-- Title Settings -->
      <div class="space-y-4">
        <h4 class="text-sm font-medium">Title</h4>

        <div class="grid gap-4 @sm:grid-cols-2">
          <div class="flex items-center justify-between">
            <Label>Show BG value</Label>
            <Switch
              checked={settings.showBgValue}
              onCheckedChange={(checked: boolean) => {
                settings.showBgValue = checked;
              }}
            />
          </div>

          <div class="flex items-center justify-between">
            <Label>Show direction arrow</Label>
            <Switch
              checked={settings.showDirection}
              onCheckedChange={(checked: boolean) => {
                settings.showDirection = checked;
              }}
            />
          </div>

          <div class="flex items-center justify-between">
            <Label>Show delta change</Label>
            <Switch
              checked={settings.showDelta}
              onCheckedChange={(checked: boolean) => {
                settings.showDelta = checked;
              }}
            />
          </div>
        </div>

        <div class="space-y-2">
          <Label for="customPrefix">Custom prefix</Label>
          <Input
            id="customPrefix"
            placeholder="e.g., Nocturne"
            value={settings.customPrefix}
            onchange={(e: Event & { currentTarget: HTMLInputElement }) => {
              settings.customPrefix = (e.target as HTMLInputElement).value;
            }}
          />
          <p class="text-xs text-muted-foreground">
            Optional text before the glucose value
          </p>
        </div>
      </div>

      <Separator />

      <!-- Favicon Settings -->
      <div class="space-y-4">
        <h4 class="text-sm font-medium">Favicon</h4>

        <div class="flex items-center justify-between">
          <div class="space-y-0.5">
            <Label>Dynamic favicon</Label>
            <p class="text-sm text-muted-foreground">
              Show a custom favicon with glucose info
            </p>
          </div>
          <Switch
            checked={settings.faviconEnabled}
            onCheckedChange={(checked: boolean) => {
              settings.faviconEnabled = checked;
            }}
          />
        </div>

        {#if settings.faviconEnabled}
          <div class="grid gap-4 @sm:grid-cols-2 pl-4">
            <div class="flex items-center justify-between">
              <Label>Show BG value in favicon</Label>
              <Switch
                checked={settings.faviconShowBg}
                onCheckedChange={(checked: boolean) => {
                  settings.faviconShowBg = checked;
                }}
              />
            </div>

            <div class="flex items-center justify-between">
              <Label>Color-code by glucose level</Label>
              <Switch
                checked={settings.faviconColorCoded}
                onCheckedChange={(checked: boolean) => {
                  settings.faviconColorCoded = checked;
                }}
              />
            </div>
          </div>
        {/if}
      </div>

      <Separator />

      <!-- Alarm Integration -->
      <div class="space-y-4">
        <h4 class="text-sm font-medium flex items-center gap-2">
          <Bell class="h-4 w-4" />
          Alarm Integration
        </h4>

        <div class="flex items-center justify-between">
          <div class="space-y-0.5">
            <Label>Flash on alarms</Label>
            <p class="text-sm text-muted-foreground">
              Flash the title and favicon during urgent glucose levels
            </p>
          </div>
          <Switch
            checked={settings.flashOnAlarm}
            onCheckedChange={(checked: boolean) => {
              settings.flashOnAlarm = checked;
            }}
          />
        </div>
      </div>
    {/if}
  </CardContent>
</Card>
