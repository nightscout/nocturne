<script lang="ts">
  import { randomUUID } from "$lib/utils";
  import { getSettingsStore } from "$lib/stores/settings-store.svelte";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Bell,
    BellOff,
    Volume2,
    VolumeX,
    Vibrate,
    AlertTriangle,
    Clock,
    Smartphone,
    Mail,
    MessageSquare,
    Plus,
    Trash2,
    Play,
    Square,
    Loader2,
    ArrowUp,
    ArrowDown,
    Pencil,
    Copy,
  } from "lucide-svelte";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";
  import BrowserCapabilities from "$lib/components/settings/BrowserCapabilities.svelte";
  import AlarmProfileDialog from "$lib/components/settings/AlarmProfileDialog.svelte";
  import WebhookChannelRow from "$lib/components/settings/notifications/webhooks/WebhookChannelRow.svelte";
  import {
    previewAlarmSound,
    stopPreview,
    getBrowserCapabilities,
    type BrowserAlarmCapabilities,
  } from "$lib/audio/alarm-sounds";
  import type { AlarmProfileConfiguration } from "$lib/types/alarm-profile";
  import { onMount } from "svelte";
  import {
    createDefaultAlarmProfile,
    ALARM_TYPE_LABELS,
    ALARM_TYPE_COLORS,
  } from "$lib/types/alarm-profile";

  const store = getSettingsStore();

  // Use the store's alarm configuration directly (synced with backend)
  // This creates a reactive reference to the store's alarmConfiguration
  let alarmConfig = $derived(store.alarmConfiguration);

  // Track save state for UI feedback
  let saveError = $state<string | null>(null);
  let saveSuccess = $state(false);

  // Dialog state
  let isDialogOpen = $state(false);
  let editingProfile = $state<AlarmProfileConfiguration | null>(null);

  let capabilities = $state<BrowserAlarmCapabilities | null>(null);
  // On component mount, check browser capabilities
  onMount(async () => {
    try {
      capabilities = await getBrowserCapabilities();
    } catch (err) {
      console.error("[Notifications] Error in onMount:", err);
    }
  });

  function openNewAlarmDialog() {
    editingProfile = createDefaultAlarmProfile("High", "New Alarm");
    isDialogOpen = true;
  }

  function openEditDialog(profile: AlarmProfileConfiguration) {
    editingProfile = profile;
    isDialogOpen = true;
  }

  async function handleSaveProfile(profile: AlarmProfileConfiguration) {
    const existingIndex = store.alarmConfiguration.profiles.findIndex(
      (p) => p.id === profile.id
    );
    if (existingIndex >= 0) {
      // Update existing
      store.alarmConfiguration.profiles[existingIndex] = profile;
    } else {
      // Add new
      profile.displayOrder = store.alarmConfiguration.profiles.length;
      store.alarmConfiguration.profiles = [
        ...store.alarmConfiguration.profiles,
        profile,
      ];
    }
    store.alarmConfiguration.profiles = [...store.alarmConfiguration.profiles]; // Trigger reactivity
    isDialogOpen = false;
    editingProfile = null;

    // Save to backend
    saveError = null;
    saveSuccess = false;
    try {
      const success = await store.saveAlarmConfiguration();
      if (success) {
        saveSuccess = true;
        setTimeout(() => (saveSuccess = false), 3000);
      } else {
        saveError = store.error ?? "Failed to save alarm configuration";
      }
    } catch (err) {
      saveError = err instanceof Error ? err.message : "Failed to save alarm configuration";
    }
  }

  function handleCancelDialog() {
    isDialogOpen = false;
    editingProfile = null;
  }

  // Track which profile is currently being previewed
  let previewingProfileId = $state<string | null>(null);

  async function handlePreviewAlarm(profile: AlarmProfileConfiguration) {
    if (previewingProfileId === profile.id) {
      // Stop if already playing this one
      stopPreview();
      previewingProfileId = null;
      return;
    }

    // Stop any current preview
    stopPreview();
    previewingProfileId = profile.id;

    try {
      await previewAlarmSound(profile.audio.soundId, {
        volume: profile.audio.maxVolume,
        ascending: profile.audio.ascendingVolume,
        startVolume: profile.audio.startVolume,
        ascendDurationSeconds: Math.min(profile.audio.ascendDurationSeconds, 5),
      });
    } finally {
      previewingProfileId = null;
    }
  }

  async function deleteProfile(id: string) {
    store.alarmConfiguration.profiles =
      store.alarmConfiguration.profiles.filter((p) => p.id !== id);
    await store.saveAlarmConfiguration();
  }

  async function duplicateProfile(profile: AlarmProfileConfiguration) {
    const copy = JSON.parse(
      JSON.stringify(profile)
    ) as AlarmProfileConfiguration;
    copy.id = randomUUID();
    copy.name = `${profile.name} (Copy)`;
    copy.displayOrder = store.alarmConfiguration.profiles.length;
    copy.createdAt = new Date().toISOString();
    copy.updatedAt = new Date().toISOString();
    store.alarmConfiguration.profiles = [
      ...store.alarmConfiguration.profiles,
      copy,
    ];
    await store.saveAlarmConfiguration();
  }

  async function toggleProfileEnabled(id: string) {
    const profile = store.alarmConfiguration.profiles.find((p) => p.id === id);
    if (profile) {
      profile.enabled = !profile.enabled;
      store.alarmConfiguration.profiles = [
        ...store.alarmConfiguration.profiles,
      ];
      await store.saveAlarmConfiguration();
    }
  }

  async function addEmergencyContact() {
    store.alarmConfiguration.emergencyContacts = [
      ...store.alarmConfiguration.emergencyContacts,
      {
        id: randomUUID(),
        name: "",
        phone: "",
        email: "",
        criticalOnly: true,
        delayMinutes: 5,
        enabled: true,
      },
    ];
    await store.saveAlarmConfiguration();
  }

  async function removeEmergencyContact(id: string) {
    store.alarmConfiguration.emergencyContacts =
      store.alarmConfiguration.emergencyContacts.filter((c) => c.id !== id);
    await store.saveAlarmConfiguration();
  }

  // Compute sorted profiles by threshold (grouped by type direction)
  const sortedProfiles = $derived(
    [...(store.alarmConfiguration.profiles || [])].sort((a, b) => {
      // Group urgents and highs together (descending threshold), lows together (ascending threshold)
      const isHighA = ["UrgentHigh", "High", "RisingFast"].includes(
        a.alarmType
      );
      const isHighB = ["UrgentHigh", "High", "RisingFast"].includes(
        b.alarmType
      );
      const isLowA = ["UrgentLow", "Low", "FallingFast"].includes(a.alarmType);
      const isLowB = ["UrgentLow", "Low", "FallingFast"].includes(b.alarmType);

      // High alarms first, then low alarms, then others
      if (isHighA && !isHighB) return -1;
      if (!isHighA && isHighB) return 1;
      if (isLowA && !isLowB) return -1;
      if (!isLowA && isLowB) return 1;

      // Within same group, sort by threshold
      if (isHighA && isHighB) return b.threshold - a.threshold; // Higher thresholds first for highs
      if (isLowA && isLowB) return a.threshold - b.threshold; // Lower thresholds first for lows

      return a.threshold - b.threshold;
    })
  );

  // Debounced save function for input fields that change frequently
  let saveTimeout: ReturnType<typeof setTimeout> | null = null;
  function debouncedSave() {
    if (saveTimeout) clearTimeout(saveTimeout);
    saveTimeout = setTimeout(() => {
      store.saveAlarmConfiguration();
    }, 1000);
  }
</script>

<svelte:head>
  <title>Alarms - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-4xl space-y-6">
  <!-- Header -->
  <div class="flex items-center justify-between">
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Alarms</h1>
      <p class="text-muted-foreground">
        Configure glucose alarms, alerts, and notification preferences
      </p>
    </div>
    {#if store.isSaving}
      <div class="flex items-center gap-2 text-muted-foreground">
        <Loader2 class="h-4 w-4 animate-spin" />
        <span class="text-sm">Saving...</span>
      </div>
    {:else if saveSuccess}
      <div class="flex items-center gap-2 text-green-600">
        <Bell class="h-4 w-4" />
        <span class="text-sm">Saved!</span>
      </div>
    {:else if saveError}
      <div class="flex items-center gap-2 text-destructive">
        <AlertTriangle class="h-4 w-4" />
        <span class="text-sm">{saveError}</span>
      </div>
    {/if}
  </div>

  {#if store.isLoading}
    <SettingsPageSkeleton cardCount={5} />
  {:else if store.hasError}
    <Card class="border-destructive">
      <CardContent class="flex items-center gap-3 pt-6">
        <AlertTriangle class="h-5 w-5 text-destructive" />
        <div>
          <p class="font-medium">Failed to load settings</p>
          <p class="text-sm text-muted-foreground">{store.error}</p>
        </div>
      </CardContent>
    </Card>
  {:else}
    <!-- Master Controls -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Bell class="h-5 w-5" />
          Global Settings
        </CardTitle>
        <CardDescription>Master controls for all notifications</CardDescription>
      </CardHeader>
      <CardContent class="space-y-6">
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-3">
            {#if alarmConfig.enabled}
              <Bell class="h-5 w-5 text-primary" />
            {:else}
              <BellOff class="h-5 w-5 text-muted-foreground" />
            {/if}
            <div>
              <Label>All Alarms</Label>
              <p class="text-sm text-muted-foreground">
                Master switch for all glucose alarms
              </p>
            </div>
          </div>
          <Switch
            checked={alarmConfig.enabled}
            onCheckedChange={(checked) => {
              store.alarmConfiguration.enabled = checked;
              debouncedSave();
            }}
          />
        </div>

        {#if alarmConfig.enabled}
          <Separator />

          <div class="grid gap-4 sm:grid-cols-2">
            <div
              class="flex items-center justify-between p-3 rounded-lg border"
            >
              <div class="flex items-center gap-3">
                <Volume2 class="h-5 w-5 text-muted-foreground" />
                <Label>Sound</Label>
              </div>
              <Switch
                checked={alarmConfig.soundEnabled}
                onCheckedChange={(checked) => {
                  store.alarmConfiguration.soundEnabled = checked;
                  debouncedSave();
                }}
              />
            </div>

            <div
              class="flex items-center justify-between p-3 rounded-lg border"
            >
              <div class="flex items-center gap-3">
                <Vibrate class="h-5 w-5 text-muted-foreground" />
                <Label>Vibration</Label>
                {#if capabilities && !capabilities.vibration}
                  <span
                    class="px-1.5 py-0.5 text-[10px] font-medium rounded bg-muted text-muted-foreground"
                  >
                    Not on this device
                  </span>
                {/if}
              </div>
              <Switch
                checked={alarmConfig.vibrationEnabled}
                onCheckedChange={(checked) => {
                  store.alarmConfiguration.vibrationEnabled = checked;
                  debouncedSave();
                }}
              />
            </div>
          </div>

          {#if alarmConfig.soundEnabled}
            <div class="space-y-2">
              <Label>Global Volume</Label>
              <div class="flex items-center gap-4">
                <VolumeX class="h-4 w-4 text-muted-foreground" />
                <input
                  type="range"
                  value={alarmConfig.globalVolume}
                  min="0"
                  max="100"
                  class="flex-1 h-2 bg-muted rounded-lg appearance-none cursor-pointer"
                  oninput={(e) => {
                    store.alarmConfiguration.globalVolume = parseInt(
                      e.currentTarget.value
                    );
                    debouncedSave();
                  }}
                />
                <Volume2 class="h-4 w-4 text-muted-foreground" />
                <span class="text-sm text-muted-foreground w-12">
                  {alarmConfig.globalVolume}%
                </span>
              </div>
            </div>
          {/if}
        {/if}
      </CardContent>
    </Card>

    <!-- Browser Capabilities -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Smartphone class="h-5 w-5" />
          Device Capabilities
        </CardTitle>
        <CardDescription>
          Check what alarm features your browser supports
        </CardDescription>
      </CardHeader>
      <CardContent>
        <BrowserCapabilities />
      </CardContent>
    </Card>

    <!-- Alarm Profiles -->
    {#if alarmConfig.enabled}
      <Card>
        <CardHeader>
          <div class="flex items-center justify-between">
            <div>
              <CardTitle class="flex items-center gap-2">
                <AlertTriangle class="h-5 w-5" />
                Alarm Profiles
              </CardTitle>
              <CardDescription>
                Customizable alarms with advanced xDrip+-style features
              </CardDescription>
            </div>
            <Button class="gap-2" onclick={openNewAlarmDialog}>
              <Plus class="h-4 w-4" />
              New Alarm
            </Button>
          </div>
        </CardHeader>
        <CardContent class="space-y-3">
          {#if sortedProfiles.length === 0}
            <div class="text-center py-12 text-muted-foreground">
              <Bell class="h-12 w-12 mx-auto mb-4 opacity-50" />
              <p class="font-medium">No alarms configured</p>
              <p class="text-sm mb-4">Create your first alarm to get started</p>
              <Button variant="outline" onclick={openNewAlarmDialog}>
                <Plus class="h-4 w-4 mr-2" />
                Create Alarm
              </Button>
            </div>
          {:else}
            {#each sortedProfiles as profile (profile.id)}
              {@const colors = ALARM_TYPE_COLORS[profile.alarmType]}
              <div
                class="flex items-center gap-4 p-4 rounded-lg border {colors.bg} {colors.border} transition-all hover:shadow-sm"
              >
                <div class="flex-1 min-w-0">
                  <div class="flex items-center gap-2 mb-1">
                    {#if profile.alarmType === "High" || profile.alarmType === "UrgentHigh" || profile.alarmType === "RisingFast"}
                      <ArrowUp class="h-4 w-4 {colors.text}" />
                    {:else if profile.alarmType === "Low" || profile.alarmType === "UrgentLow" || profile.alarmType === "FallingFast"}
                      <ArrowDown class="h-4 w-4 {colors.text}" />
                    {:else}
                      <AlertTriangle class="h-4 w-4 {colors.text}" />
                    {/if}
                    <span class="font-medium truncate">{profile.name}</span>
                    <span class="text-xs px-2 py-0.5 rounded bg-background/50">
                      {ALARM_TYPE_LABELS[profile.alarmType]}
                    </span>
                    {#if profile.priority === "Critical"}
                      <span
                        class="text-xs px-2 py-0.5 rounded bg-red-500 text-white"
                      >
                        Critical
                      </span>
                    {/if}
                  </div>
                  <div
                    class="flex items-center gap-4 text-sm text-muted-foreground"
                  >
                    <span>
                      {profile.alarmType === "StaleData"
                        ? `${profile.threshold} min`
                        : `${profile.threshold} mg/dL`}
                    </span>
                    {#if profile.persistenceMinutes > 0}
                      <span class="flex items-center gap-1">
                        <Clock class="h-3 w-3" />
                        {profile.persistenceMinutes}m delay
                      </span>
                    {/if}
                    {#if profile.audio.ascendingVolume}
                      <span class="flex items-center gap-1">
                        <Volume2 class="h-3 w-3" />
                        Ascending
                      </span>
                    {/if}
                    {#if profile.smartSnooze.enabled}
                      <span class="text-primary">Smart Snooze</span>
                    {/if}
                    {#if profile.schedule.enabled}
                      <span class="flex items-center gap-1">
                        <Clock class="h-3 w-3" />
                        Scheduled
                      </span>
                    {/if}
                  </div>
                </div>

                <div class="flex items-center gap-2">
                  <Button
                    variant={previewingProfileId === profile.id
                      ? "default"
                      : "ghost"}
                    size="icon"
                    class={previewingProfileId === profile.id
                      ? "animate-pulse"
                      : ""}
                    onclick={() => handlePreviewAlarm(profile)}
                    title={previewingProfileId === profile.id
                      ? "Stop preview"
                      : "Preview alarm"}
                  >
                    {#if previewingProfileId === profile.id}
                      <Square class="h-4 w-4 fill-current" />
                    {:else}
                      <Play class="h-4 w-4" />
                    {/if}
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon"
                    onclick={() => duplicateProfile(profile)}
                    title="Duplicate"
                  >
                    <Copy class="h-4 w-4" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon"
                    onclick={() => openEditDialog(profile)}
                    title="Edit"
                  >
                    <Pencil class="h-4 w-4" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon"
                    class="text-destructive"
                    onclick={() => deleteProfile(profile.id)}
                    title="Delete"
                  >
                    <Trash2 class="h-4 w-4" />
                  </Button>
                  <Switch
                    checked={profile.enabled}
                    onCheckedChange={() => toggleProfileEnabled(profile.id)}
                  />
                </div>
              </div>
            {/each}
          {/if}
        </CardContent>
      </Card>

      <!-- Quiet Hours -->
      <Card>
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <Clock class="h-5 w-5" />
            Quiet Hours
          </CardTitle>
          <CardDescription>
            Reduce notification noise during specified hours
          </CardDescription>
        </CardHeader>
        <CardContent class="space-y-4">
          <div class="flex items-center justify-between">
            <Label>Enable quiet hours</Label>
            <Switch
              checked={alarmConfig.quietHours.enabled}
              onCheckedChange={(checked) => {
                store.alarmConfiguration.quietHours.enabled = checked;
                debouncedSave();
              }}
            />
          </div>
          {#if alarmConfig.quietHours.enabled}
            <div class="grid gap-4 sm:grid-cols-2">
              <div class="space-y-2">
                <Label>Start time</Label>
                <Input
                  type="time"
                  value={alarmConfig.quietHours.startTime}
                  onchange={(e) => {
                    store.alarmConfiguration.quietHours.startTime =
                      e.currentTarget.value;
                    debouncedSave();
                  }}
                />
              </div>
              <div class="space-y-2">
                <Label>End time</Label>
                <Input
                  type="time"
                  value={alarmConfig.quietHours.endTime}
                  onchange={(e) => {
                    store.alarmConfiguration.quietHours.endTime =
                      e.currentTarget.value;
                    debouncedSave();
                  }}
                />
              </div>
            </div>

            <div class="flex items-center justify-between">
              <div>
                <Label>Allow Critical Alarms</Label>
                <p class="text-sm text-muted-foreground">
                  Critical priority alarms will still sound
                </p>
              </div>
              <Switch
                checked={alarmConfig.quietHours.allowCritical}
                onCheckedChange={(checked) => {
                  store.alarmConfiguration.quietHours.allowCritical = checked;
                  debouncedSave();
                }}
              />
            </div>

            <div class="flex items-center justify-between">
              <div>
                <Label>Reduce Volume Instead</Label>
                <p class="text-sm text-muted-foreground">
                  Play alarms at reduced volume rather than silencing
                </p>
              </div>
              <Switch
                checked={alarmConfig.quietHours.reduceVolume}
                onCheckedChange={(checked) => {
                  store.alarmConfiguration.quietHours.reduceVolume = checked;
                  debouncedSave();
                }}
              />
            </div>

            {#if alarmConfig.quietHours.reduceVolume}
              <div class="space-y-2">
                <Label>Quiet Hours Volume</Label>
                <div class="flex items-center gap-4">
                  <VolumeX class="h-4 w-4 text-muted-foreground" />
                  <input
                    type="range"
                    value={alarmConfig.quietHours.quietVolume}
                    min="0"
                    max="100"
                    class="flex-1 h-2 bg-muted rounded-lg appearance-none cursor-pointer"
                    oninput={(e) => {
                      store.alarmConfiguration.quietHours.quietVolume =
                        parseInt(e.currentTarget.value);
                      debouncedSave();
                    }}
                  />
                  <Volume2 class="h-4 w-4 text-muted-foreground" />
                  <span class="text-sm text-muted-foreground w-12">
                    {alarmConfig.quietHours.quietVolume}%
                  </span>
                </div>
              </div>
            {/if}
          {/if}
        </CardContent>
      </Card>
    {/if}

    <!-- Notification Channels -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Smartphone class="h-5 w-5" />
          Notification Channels
        </CardTitle>
        <CardDescription>Choose how you receive notifications</CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="flex items-center justify-between p-3 rounded-lg border">
          <div class="flex items-center gap-3">
            <Bell class="h-5 w-5 text-muted-foreground" />
            <div>
              <Label>Push Notifications</Label>
              <p class="text-sm text-muted-foreground">Alerts on this device</p>
            </div>
          </div>
          <Switch
            checked={alarmConfig.channels.push.enabled}
            onCheckedChange={(checked) => {
              store.alarmConfiguration.channels.push.enabled = checked;
              debouncedSave();
            }}
          />
        </div>

        <div class="flex items-center justify-between p-3 rounded-lg border">
          <div class="flex items-center gap-3">
            <Mail class="h-5 w-5 text-muted-foreground" />
            <div>
              <Label>Email</Label>
              <p class="text-sm text-muted-foreground">
                Daily summaries and urgent alerts
              </p>
            </div>
          </div>
          <Switch
            checked={alarmConfig.channels.email.enabled}
            onCheckedChange={(checked) => {
              store.alarmConfiguration.channels.email.enabled = checked;
              debouncedSave();
            }}
          />
        </div>

        <div class="flex items-center justify-between p-3 rounded-lg border">
          <div class="flex items-center gap-3">
            <MessageSquare class="h-5 w-5 text-muted-foreground" />
            <div>
              <Label>SMS</Label>
              <p class="text-sm text-muted-foreground">
                Text messages for urgent alerts
              </p>
            </div>
          </div>
          <Switch
            checked={alarmConfig.channels.sms.enabled}
            onCheckedChange={(checked) => {
              store.alarmConfiguration.channels.sms.enabled = checked;
              debouncedSave();
            }}
          />
        </div>

        <WebhookChannelRow />
      </CardContent>
    </Card>

    <!-- Emergency Contacts -->
    <Card>
      <CardHeader>
        <div class="flex items-center justify-between">
          <div>
            <CardTitle>Emergency Contacts</CardTitle>
            <CardDescription>
              People to notify during urgent glucose events
            </CardDescription>
          </div>
          <Button
            size="sm"
            variant="outline"
            class="gap-2"
            onclick={addEmergencyContact}
          >
            <Plus class="h-4 w-4" />
            Add Contact
          </Button>
        </div>
      </CardHeader>
      <CardContent class="space-y-4">
        {#if alarmConfig.emergencyContacts.length === 0}
          <div class="text-center py-8 text-muted-foreground">
            <MessageSquare class="h-12 w-12 mx-auto mb-4 opacity-50" />
            <p class="font-medium">No emergency contacts</p>
            <p class="text-sm">Add contacts to notify during urgent events</p>
          </div>
        {:else}
          {#each alarmConfig.emergencyContacts as contact, index}
            <div class="flex items-start gap-4 p-4 rounded-lg border">
              <div class="flex-1 grid gap-4 sm:grid-cols-2">
                <div class="space-y-2">
                  <Label>Name</Label>
                  <Input
                    value={contact.name}
                    placeholder="Contact name"
                    onchange={(e) => {
                      store.alarmConfiguration.emergencyContacts[index].name =
                        e.currentTarget.value;
                      debouncedSave();
                    }}
                  />
                </div>
                <div class="space-y-2">
                  <Label>Phone</Label>
                  <Input
                    value={contact.phone ?? ""}
                    placeholder="+1 555-0000"
                    onchange={(e) => {
                      store.alarmConfiguration.emergencyContacts[index].phone =
                        e.currentTarget.value;
                      debouncedSave();
                    }}
                  />
                </div>
                <div class="space-y-2">
                  <Label>Email</Label>
                  <Input
                    type="email"
                    value={contact.email ?? ""}
                    placeholder="email@example.com"
                    onchange={(e) => {
                      store.alarmConfiguration.emergencyContacts[index].email =
                        e.currentTarget.value;
                      debouncedSave();
                    }}
                  />
                </div>
                <div class="space-y-2">
                  <Label>Delay before notification</Label>
                  <div class="flex items-center gap-2">
                    <Input
                      type="number"
                      value={contact.delayMinutes}
                      class="w-20"
                      min="0"
                      onchange={(e) => {
                        store.alarmConfiguration.emergencyContacts[
                          index
                        ].delayMinutes = parseInt(e.currentTarget.value);
                        debouncedSave();
                      }}
                    />
                    <span class="text-sm text-muted-foreground">minutes</span>
                  </div>
                </div>
              </div>
              <div class="flex flex-col items-end gap-2">
                <div class="flex items-center gap-2">
                  <Switch
                    checked={contact.criticalOnly}
                    onCheckedChange={(checked) => {
                      store.alarmConfiguration.emergencyContacts[
                        index
                      ].criticalOnly = checked;
                      debouncedSave();
                    }}
                  />
                  <Label class="text-sm">Critical only</Label>
                </div>
                <div class="flex items-center gap-2">
                  <Switch
                    checked={contact.enabled}
                    onCheckedChange={(checked) => {
                      store.alarmConfiguration.emergencyContacts[
                        index
                      ].enabled = checked;
                      debouncedSave();
                    }}
                  />
                  <Label class="text-sm">Enabled</Label>
                </div>
                <Button
                  variant="ghost"
                  size="icon"
                  class="text-destructive mt-2"
                  onclick={() => removeEmergencyContact(contact.id)}
                >
                  <Trash2 class="h-4 w-4" />
                </Button>
              </div>
            </div>
          {/each}
        {/if}
      </CardContent>
    </Card>
  {/if}
</div>

<!-- Alarm Profile Editor Dialog -->
{#if editingProfile}
  <AlarmProfileDialog
    bind:open={isDialogOpen}
    profile={editingProfile}
    emergencyContacts={store.alarmConfiguration.emergencyContacts}
    onSave={handleSaveProfile}
    onCancel={handleCancelDialog}
  />
{/if}
