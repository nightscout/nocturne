<script lang="ts">
  import { onMount } from "svelte";
  import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
  } from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Tabs,
    TabsContent,
    TabsList,
    TabsTrigger,
  } from "$lib/components/ui/tabs";
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectGroup,
    SelectLabel,
  } from "$lib/components/ui/select";
  import {
    Bell,
    Volume2,
    VolumeX,
    Vibrate,
    Eye,
    Clock,
    Settings2,
    TrendingUp,
    Timer,
    RotateCcw,
    Sparkles,
    Music,
    Upload,
  } from "lucide-svelte";
  import type {
    AlarmProfileConfiguration,
    AlarmTriggerType,
    AlarmPriority,
    EmergencyContactConfig,
  } from "$lib/types/alarm-profile";
  import {
    BUILT_IN_SOUNDS,
    ALARM_TYPE_LABELS,
    normalizeAlarmType,
    PRIORITY_LABELS,
  } from "$lib/types/alarm-profile";
  import {
    getAllAlarmSounds,
    isCustomSound,
    getBrowserCapabilities,
    type BrowserAlarmCapabilities,
  } from "$lib/audio/alarm-sounds";
  import AlarmPreview from "./AlarmPreview.svelte";
  import CustomSoundUpload from "./CustomSoundUpload.svelte";

  interface Props {
    open: boolean;
    profile: AlarmProfileConfiguration;
    emergencyContacts?: EmergencyContactConfig[];
    onSave: (profile: AlarmProfileConfiguration) => void;
    onCancel: () => void;
  }

  let {
    open = $bindable(),
    profile,
    emergencyContacts = [],
    onSave,
    onCancel,
  }: Props = $props();

  // Helper to create a properly initialized profile copy with all defaults
  function initializeProfile(
    p: AlarmProfileConfiguration
  ): AlarmProfileConfiguration {
    const copy = JSON.parse(JSON.stringify(p)) as AlarmProfileConfiguration;
    copy.alarmType = normalizeAlarmType(copy.alarmType);
    // Ensure schedule.activeDays is initialized
    if (!copy.schedule.activeDays) {
      copy.schedule.activeDays = [];
    }
    // Ensure visual.showEmergencyContacts is initialized
    if (copy.visual.showEmergencyContacts === undefined) {
      copy.visual.showEmergencyContacts =
        p.alarmType === "UrgentLow" || p.alarmType === "UrgentHigh";
    }
    return copy;
  }

  // Create a working copy of the profile
  // svelte-ignore state_referenced_locally
  let editedProfile = $state<AlarmProfileConfiguration>(
    initializeProfile(profile)
  );

  // All available sounds (built-in + custom)
  let allSounds = $state<
    Array<{ id: string; name: string; description: string; isCustom: boolean }>
  >([]);
  let showCustomSoundUpload = $state(false);

  // Browser capabilities for showing unavailable feature chips
  let capabilities = $state<BrowserAlarmCapabilities | null>(null);

  // Load all sounds and capabilities on mount
  onMount(async () => {
    allSounds = await getAllAlarmSounds();
    capabilities = getBrowserCapabilities();
  });

  // Track previous state to avoid unnecessary re-runs
  let lastOpenState = false;
  let lastProfileId = "";

  // Handle dialog open - reload sounds and reset profile
  $effect(() => {
    const isOpen = open;
    const profileId = profile.id;

    // Only run when dialog is opening (not on every render)
    if (isOpen && !lastOpenState) {
      // Reload sounds in case custom sounds were added
      getAllAlarmSounds().then((sounds) => {
        allSounds = sounds;
      });

      // Reset edited profile with proper defaults
      editedProfile = initializeProfile(profile);
      lastProfileId = profileId;
    } else if (isOpen && profileId !== lastProfileId) {
      // Profile changed while dialog is open
      editedProfile = initializeProfile(profile);
      lastProfileId = profileId;
    }

    lastOpenState = isOpen;
  });

  // Get the name of the currently selected sound
  function getSelectedSoundName(): string {
    const sound = allSounds.find((s) => s.id === editedProfile.audio.soundId);
    if (sound) return sound.name;
    // Fallback to built-in sounds if allSounds not loaded yet
    const builtIn = BUILT_IN_SOUNDS.find(
      (s) => s.id === editedProfile.audio.soundId
    );
    return builtIn?.name ?? "Select sound";
  }

  const alarmTypes: AlarmTriggerType[] = [
    "UrgentLow",
    "Low",
    "High",
    "UrgentHigh",
    "ForecastLow",
    "RisingFast",
    "FallingFast",
    "StaleData",
    "Custom",
  ];

  const priorities: AlarmPriority[] = ["Low", "Normal", "High", "Critical"];

  const vibrationPatterns = [
    { value: "short", label: "Short" },
    { value: "long", label: "Long" },
    { value: "sos", label: "SOS" },
    { value: "continuous", label: "Continuous" },
  ];

  const daysOfWeek = [
    { value: 0, label: "Sun" },
    { value: 1, label: "Mon" },
    { value: 2, label: "Tue" },
    { value: 3, label: "Wed" },
    { value: 4, label: "Thu" },
    { value: 5, label: "Fri" },
    { value: 6, label: "Sat" },
  ];

  function handleSave() {
    editedProfile.updatedAt = new Date().toISOString();
    onSave(editedProfile);
  }

  function toggleDay(day: number) {
    if (!editedProfile.schedule.activeDays) {
      editedProfile.schedule.activeDays = [];
    }
    const index = editedProfile.schedule.activeDays.indexOf(day);
    if (index >= 0) {
      editedProfile.schedule.activeDays =
        editedProfile.schedule.activeDays.filter((d) => d !== day);
    } else {
      editedProfile.schedule.activeDays = [
        ...editedProfile.schedule.activeDays,
        day,
      ];
    }
  }

  function addTimeRange() {
    editedProfile.schedule.activeRanges = [
      ...editedProfile.schedule.activeRanges,
      { startTime: "09:00", endTime: "17:00" },
    ];
  }

  function removeTimeRange(index: number) {
    editedProfile.schedule.activeRanges =
      editedProfile.schedule.activeRanges.filter((_, i) => i !== index);
  }

  function addSnoozeOption(minutes: number) {
    if (!editedProfile.snooze.options.includes(minutes)) {
      editedProfile.snooze.options = [
        ...editedProfile.snooze.options,
        minutes,
      ].sort((a, b) => a - b);
    }
  }

  function removeSnoozeOption(minutes: number) {
    editedProfile.snooze.options = editedProfile.snooze.options.filter(
      (m) => m !== minutes
    );
  }
</script>

<Dialog bind:open>
  <DialogContent
    class="max-w-3xl sm:max-w-5xl max-h-[90vh] overflow-hidden flex flex-col"
  >
    <DialogHeader>
      <DialogTitle class="flex items-center gap-2">
        <Bell class="h-5 w-5" />
        {editedProfile.id ? "Edit Alarm" : "New Alarm"}
      </DialogTitle>
      <DialogDescription>
        Configure all aspects of this alarm including sounds, visuals, and smart
        behaviors.
      </DialogDescription>
    </DialogHeader>

    <div class="flex-1 overflow-y-auto py-4">
      <Tabs value="general" class="w-full">
        <TabsList class="grid w-full grid-cols-5">
          <TabsTrigger value="general">
            <Settings2 class="h-4 w-4 mr-2" />
            General
          </TabsTrigger>
          <TabsTrigger value="audio">
            <Volume2 class="h-4 w-4 mr-2" />
            Audio
          </TabsTrigger>
          <TabsTrigger value="visual">
            <Eye class="h-4 w-4 mr-2" />
            Visual
          </TabsTrigger>
          <TabsTrigger value="snooze">
            <Timer class="h-4 w-4 mr-2" />
            Snooze
          </TabsTrigger>
          <TabsTrigger value="schedule">
            <Clock class="h-4 w-4 mr-2" />
            Schedule
          </TabsTrigger>
        </TabsList>

        <!-- General Tab -->
        <TabsContent value="general" class="space-y-6 mt-6">
          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-2">
              <Label for="name">Alarm Name</Label>
              <Input
                id="name"
                bind:value={editedProfile.name}
                placeholder="e.g., Nighttime Low"
              />
            </div>
            <div class="space-y-2">
              <Label for="description">Description</Label>
              <Input
                id="description"
                bind:value={editedProfile.description}
                placeholder="Optional description"
              />
            </div>
          </div>

          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-2">
              <Label>Alarm Type</Label>
              <Select
                type="single"
                value={editedProfile.alarmType}
                onValueChange={(value) => {
                  if (value) {
                    editedProfile.alarmType = normalizeAlarmType(value);

                    // Auto-fill emergency instructions for Urgent Low if empty
                    if (
                      editedProfile.alarmType === "UrgentLow" &&
                      !editedProfile.visual.emergencyInstructions
                    ) {
                      editedProfile.visual.emergencyInstructions =
                        "Administer carbs ONLY if they are conscious and able to swallow by themselves.";
                      editedProfile.visual.showEmergencyContacts = true;
                    }
                  }
                }}
              >
                <SelectTrigger>
                  <span>{ALARM_TYPE_LABELS[editedProfile.alarmType]}</span>
                </SelectTrigger>
                <SelectContent>
                  {#each alarmTypes as type}
                    <SelectItem value={type}>
                      {ALARM_TYPE_LABELS[type] ?? type}
                    </SelectItem>
                  {/each}
                </SelectContent>
              </Select>
            </div>
            <div class="space-y-2">
              <Label>Priority</Label>
              <Select
                type="single"
                value={editedProfile.priority}
                onValueChange={(value) => {
                  if (value) editedProfile.priority = value as AlarmPriority;
                }}
              >
                <SelectTrigger>
                  <span>{PRIORITY_LABELS[editedProfile.priority]}</span>
                </SelectTrigger>
                <SelectContent>
                  {#each priorities as priority}
                    <SelectItem value={priority}>
                      {PRIORITY_LABELS[priority] ?? priority}
                    </SelectItem>
                  {/each}
                </SelectContent>
              </Select>
            </div>
          </div>

          <Separator />

          <div class="space-y-4">
            <h4 class="font-medium">Threshold Settings</h4>
            <div class="grid gap-4 sm:grid-cols-2">
              <div class="space-y-2">
                <Label>
                  {editedProfile.alarmType === "StaleData"
                    ? "Minutes without data"
                    : "Threshold"}
                </Label>
                <div class="flex items-center gap-2">
                  <Input
                    type="number"
                    bind:value={editedProfile.threshold}
                    class="w-24"
                  />
                  <span class="text-sm text-muted-foreground">
                    {editedProfile.alarmType === "StaleData"
                      ? "min"
                      : editedProfile.alarmType === "RisingFast" ||
                          editedProfile.alarmType === "FallingFast"
                        ? "mg/dL/min"
                        : "mg/dL"}
                  </span>
                </div>
              </div>

              {#if editedProfile.alarmType === "Custom"}
                <div class="space-y-2">
                  <Label>Upper Threshold</Label>
                  <div class="flex items-center gap-2">
                    <Input
                      type="number"
                      bind:value={editedProfile.thresholdHigh}
                      class="w-24"
                    />
                    <span class="text-sm text-muted-foreground">mg/dL</span>
                  </div>
                </div>
              {/if}

              {#if editedProfile.alarmType === "ForecastLow"}
                <div class="space-y-2">
                  <Label>Lead Time</Label>
                  <div class="flex items-center gap-2">
                    <Input
                      type="number"
                      bind:value={editedProfile.forecastLeadTimeMinutes}
                      class="w-24"
                      min="5"
                      max="60"
                      step="5"
                    />
                    <span class="text-sm text-muted-foreground">minutes</span>
                  </div>
                  <p class="text-xs text-muted-foreground">
                    How far ahead to predict (5-60 min). Alarm triggers if glucose is forecast to drop below threshold within this time.
                  </p>
                </div>
              {/if}
            </div>

            <div class="space-y-2">
              <div class="flex items-center gap-2">
                <Timer class="h-4 w-4 text-muted-foreground" />
                <Label>Delayed Raise (Persistence)</Label>
              </div>
              <p class="text-sm text-muted-foreground mb-2">
                Only trigger alarm if condition persists for this long
              </p>
              <div class="flex items-center gap-2">
                <Input
                  type="number"
                  bind:value={editedProfile.persistenceMinutes}
                  class="w-24"
                  min="0"
                />
                <span class="text-sm text-muted-foreground">
                  minutes (0 = immediate)
                </span>
              </div>
            </div>
          </div>

          <Separator />

          <div class="flex items-center justify-between">
            <div>
              <Label>Override Quiet Hours</Label>
              <p class="text-sm text-muted-foreground">
                This alarm will sound even during quiet hours
              </p>
            </div>
            <Switch bind:checked={editedProfile.overrideQuietHours} />
          </div>
        </TabsContent>

        <!-- Audio Tab -->
        <TabsContent value="audio" class="space-y-6 mt-6">
          <div class="flex items-center justify-between">
            <div class="flex items-center gap-3">
              <Volume2 class="h-5 w-5 text-muted-foreground" />
              <div>
                <Label>Sound Enabled</Label>
                <p class="text-sm text-muted-foreground">
                  Play audio when alarm triggers
                </p>
              </div>
            </div>
            <Switch bind:checked={editedProfile.audio.enabled} />
          </div>

          {#if editedProfile.audio.enabled}
            <Separator />

            <div class="space-y-4">
              <div class="space-y-2">
                <div class="flex items-center justify-between">
                  <Label>Alarm Sound</Label>
                  <Button
                    variant="ghost"
                    size="sm"
                    onclick={() =>
                      (showCustomSoundUpload = !showCustomSoundUpload)}
                    class="h-7 text-xs"
                  >
                    <Upload class="h-3 w-3 mr-1" />
                    {showCustomSoundUpload ? "Hide" : "Manage"} Custom Sounds
                  </Button>
                </div>
                <div class="flex items-center gap-2">
                  <Select
                    type="single"
                    value={editedProfile.audio.soundId}
                    onValueChange={(value) => {
                      if (value) editedProfile.audio.soundId = value;
                    }}
                  >
                    <SelectTrigger class="flex-1">
                      <span class="flex items-center gap-2">
                        {#if isCustomSound(editedProfile.audio.soundId)}
                          <Music class="h-3 w-3" />
                        {/if}
                        {getSelectedSoundName()}
                      </span>
                    </SelectTrigger>
                    <SelectContent>
                      <SelectGroup>
                        <SelectLabel>Built-in Sounds</SelectLabel>
                        {#each BUILT_IN_SOUNDS as sound}
                          <SelectItem value={sound.id}>{sound.name}</SelectItem>
                        {/each}
                      </SelectGroup>
                      {#if allSounds.some((s) => s.isCustom)}
                        <SelectGroup>
                          <SelectLabel>Custom Sounds</SelectLabel>
                          {#each allSounds.filter((s) => s.isCustom) as sound}
                            <SelectItem value={sound.id}>
                              <span class="flex items-center gap-2">
                                <Music class="h-3 w-3" />
                                {sound.name}
                              </span>
                            </SelectItem>
                          {/each}
                        </SelectGroup>
                      {/if}
                    </SelectContent>
                  </Select>
                </div>
              </div>

              <!-- Custom Sound Upload Section -->
              {#if showCustomSoundUpload}
                <div class="p-4 rounded-lg border bg-muted/30">
                  <CustomSoundUpload
                    selectedSoundId={editedProfile.audio.soundId}
                    onSoundSelected={(id) => {
                      editedProfile.audio.soundId = id;
                      // Refresh sound list
                      getAllAlarmSounds().then(
                        (sounds) => (allSounds = sounds)
                      );
                    }}
                  />
                </div>
              {/if}

              <!-- Preview Section -->
              <div class="p-4 rounded-lg border bg-muted/30">
                <div class="flex items-center justify-between mb-3">
                  <Label>Test Alarm</Label>
                  <span class="text-xs text-muted-foreground">
                    Preview sound and visual effects
                  </span>
                </div>
                <AlarmPreview
                  profile={editedProfile}
                  isOpen={open}
                  {emergencyContacts}
                />
              </div>

              <div
                class="flex items-center justify-between p-4 rounded-lg border"
              >
                <div class="flex items-center gap-3">
                  <TrendingUp class="h-5 w-5 text-muted-foreground" />
                  <div>
                    <Label>Ascending Volume</Label>
                    <p class="text-sm text-muted-foreground">
                      Start quiet and gradually get louder
                    </p>
                  </div>
                </div>
                <Switch bind:checked={editedProfile.audio.ascendingVolume} />
              </div>

              {#if editedProfile.audio.ascendingVolume}
                <div
                  class="grid gap-4 sm:grid-cols-3 p-4 bg-muted/50 rounded-lg"
                >
                  <div class="space-y-2">
                    <Label class="text-sm">Start Volume</Label>
                    <div class="flex items-center gap-2">
                      <Input
                        type="number"
                        bind:value={editedProfile.audio.startVolume}
                        class="w-20"
                        min="0"
                        max="100"
                      />
                      <span class="text-sm text-muted-foreground">%</span>
                    </div>
                  </div>
                  <div class="space-y-2">
                    <Label class="text-sm">Max Volume</Label>
                    <div class="flex items-center gap-2">
                      <Input
                        type="number"
                        bind:value={editedProfile.audio.maxVolume}
                        class="w-20"
                        min="0"
                        max="100"
                      />
                      <span class="text-sm text-muted-foreground">%</span>
                    </div>
                  </div>
                  <div class="space-y-2">
                    <Label class="text-sm">Ramp Duration</Label>
                    <div class="flex items-center gap-2">
                      <Input
                        type="number"
                        bind:value={editedProfile.audio.ascendDurationSeconds}
                        class="w-20"
                        min="5"
                      />
                      <span class="text-sm text-muted-foreground">sec</span>
                    </div>
                  </div>
                </div>
              {:else}
                <div class="space-y-2">
                  <Label>Volume</Label>
                  <div class="flex items-center gap-4">
                    <VolumeX class="h-4 w-4 text-muted-foreground" />
                    <input
                      type="range"
                      bind:value={editedProfile.audio.maxVolume}
                      min="0"
                      max="100"
                      class="flex-1 h-2 bg-muted rounded-lg appearance-none cursor-pointer"
                    />
                    <Volume2 class="h-4 w-4 text-muted-foreground" />
                    <span class="text-sm text-muted-foreground w-12">
                      {editedProfile.audio.maxVolume}%
                    </span>
                  </div>
                </div>
              {/if}
            </div>

            <Separator />

            <div class="flex items-center justify-between">
              <div class="flex items-center gap-3">
                <Vibrate class="h-5 w-5 text-muted-foreground" />
                <div>
                  <div class="flex items-center gap-2">
                    <Label>Vibration</Label>
                    {#if capabilities && !capabilities.vibration}
                      <span
                        class="px-1.5 py-0.5 text-[10px] font-medium rounded bg-muted text-muted-foreground"
                      >
                        Not on this device
                      </span>
                    {/if}
                  </div>
                  <p class="text-sm text-muted-foreground">
                    Vibrate device when alarm triggers
                  </p>
                </div>
              </div>
              <Switch bind:checked={editedProfile.vibration.enabled} />
            </div>

            {#if editedProfile.vibration.enabled}
              <div class="space-y-2">
                <Label>Vibration Pattern</Label>
                <Select
                  type="single"
                  value={editedProfile.vibration.pattern}
                  onValueChange={(value) => {
                    if (value) editedProfile.vibration.pattern = value;
                  }}
                >
                  <SelectTrigger>
                    <span>
                      {vibrationPatterns.find(
                        (p) => p.value === editedProfile.vibration.pattern
                      )?.label}
                    </span>
                  </SelectTrigger>
                  <SelectContent>
                    {#each vibrationPatterns as pattern}
                      <SelectItem value={pattern.value}>
                        {pattern.label}
                      </SelectItem>
                    {/each}
                  </SelectContent>
                </Select>
              </div>
            {/if}
          {/if}
        </TabsContent>

        <!-- Visual Tab -->
        <TabsContent value="visual" class="space-y-6 mt-6">
          <!-- Preview Section -->
          <div class="p-4 rounded-lg border bg-muted/30">
            <div class="flex items-center justify-between mb-3">
              <Label>Test Alarm</Label>
              <span class="text-xs text-muted-foreground">
                Preview sound and visual effects
              </span>
            </div>
            <AlarmPreview
              profile={editedProfile}
              isOpen={open}
              {emergencyContacts}
            />
          </div>
          <div class="flex items-center justify-between">
            <div class="flex items-center gap-3">
              <Eye class="h-5 w-5 text-muted-foreground" />
              <div>
                <Label>Screen Flash</Label>
                <p class="text-sm text-muted-foreground">
                  Flash the screen to get attention
                </p>
              </div>
            </div>
            <Switch bind:checked={editedProfile.visual.screenFlash} />
          </div>

          {#if editedProfile.visual.screenFlash}
            <div class="grid gap-4 sm:grid-cols-2 p-4 bg-muted/50 rounded-lg">
              <div class="space-y-2">
                <Label>Flash Color</Label>
                <div class="flex items-center gap-2">
                  <input
                    type="color"
                    bind:value={editedProfile.visual.flashColor}
                    class="w-12 h-10 rounded border cursor-pointer"
                  />
                  <Input
                    bind:value={editedProfile.visual.flashColor}
                    class="flex-1"
                    placeholder="#ff0000"
                  />
                </div>
              </div>
              <div class="space-y-2">
                <Label>Flash Interval</Label>
                <div class="flex items-center gap-2">
                  <Input
                    type="number"
                    bind:value={editedProfile.visual.flashIntervalMs}
                    class="w-24"
                    min="100"
                    step="100"
                  />
                  <span class="text-sm text-muted-foreground">ms</span>
                </div>
              </div>
            </div>
          {/if}

          <Separator />

          <div class="flex items-center justify-between">
            <div>
              <div class="flex items-center gap-2">
                <Label>Persistent Banner</Label>
                {#if capabilities && !capabilities.notifications}
                  <span
                    class="px-1.5 py-0.5 text-[10px] font-medium rounded bg-muted text-muted-foreground"
                  >
                    Not on this device
                  </span>
                {:else if capabilities && capabilities.notificationPermission === "denied"}
                  <span
                    class="px-1.5 py-0.5 text-[10px] font-medium rounded bg-red-500/10 text-red-500"
                  >
                    Blocked on this device
                  </span>
                {:else if capabilities && capabilities.notificationPermission === "default"}
                  <span
                    class="px-1.5 py-0.5 text-[10px] font-medium rounded bg-yellow-500/10 text-yellow-600 dark:text-yellow-400"
                  >
                    Needs Permission
                  </span>
                {/if}
              </div>
              <p class="text-sm text-muted-foreground">
                Show notification banner until acknowledged
              </p>
            </div>
            <Switch bind:checked={editedProfile.visual.persistentBanner} />
          </div>

          <div class="flex items-center justify-between">
            <div>
              <div class="flex items-center gap-2">
                <Label>Wake Screen</Label>
                {#if capabilities && !capabilities.wakeLock}
                  <span
                    class="px-1.5 py-0.5 text-[10px] font-medium rounded bg-muted text-muted-foreground"
                  >
                    Not on this device
                  </span>
                {/if}
              </div>
              <p class="text-sm text-muted-foreground">
                Turn on the screen when alarm triggers
              </p>
            </div>
            <Switch bind:checked={editedProfile.visual.wakeScreen} />
          </div>

          <Separator />

          <div class="flex items-center justify-between">
            <div>
              <Label>Show Emergency Contacts</Label>
              <p class="text-sm text-muted-foreground">
                Display "In Case of Emergency, Contact:" info during alarm
              </p>
            </div>
            <Switch bind:checked={editedProfile.visual.showEmergencyContacts} />
          </div>

          {#if editedProfile.visual.showEmergencyContacts}
            <div class="space-y-2 animate-in slide-in-from-top-2 duration-200">
              <Label>Specific Instructions</Label>
              <Textarea
                bind:value={editedProfile.visual.emergencyInstructions}
                placeholder="e.g. Spare key is under the mat..."
                class="resize-none h-24"
              />
              <p class="text-xs text-muted-foreground">
                Instructions to display to your emergency contacts.
              </p>
            </div>
          {/if}
        </TabsContent>

        <!-- Snooze Tab -->
        <TabsContent value="snooze" class="space-y-6 mt-6">
          <div class="space-y-4">
            <h4 class="font-medium flex items-center gap-2">
              <Timer class="h-4 w-4" />
              Snooze Settings
            </h4>

            <div class="grid gap-4 sm:grid-cols-2">
              <div class="space-y-2">
                <Label>Default Snooze</Label>
                <div class="flex items-center gap-2">
                  <Input
                    type="number"
                    bind:value={editedProfile.snooze.defaultMinutes}
                    class="w-24"
                    min="1"
                  />
                  <span class="text-sm text-muted-foreground">minutes</span>
                </div>
              </div>
              <div class="space-y-2">
                <Label>Maximum Snooze</Label>
                <div class="flex items-center gap-2">
                  <Input
                    type="number"
                    bind:value={editedProfile.snooze.maxMinutes}
                    class="w-24"
                    min="1"
                  />
                  <span class="text-sm text-muted-foreground">minutes</span>
                </div>
              </div>
            </div>

            <div class="space-y-2">
              <Label>Quick Snooze Options</Label>
              <div class="flex flex-wrap gap-2">
                {#each editedProfile.snooze.options as minutes}
                  <span
                    class="bg-primary/10 text-primary px-3 py-1 rounded-full text-sm flex items-center gap-2"
                  >
                    {minutes}m
                    <button
                      class="hover:text-destructive"
                      onclick={() => removeSnoozeOption(minutes)}
                    >
                      ×
                    </button>
                  </span>
                {/each}
                <Select
                  type="single"
                  onValueChange={(value) => {
                    if (value) addSnoozeOption(parseInt(value));
                  }}
                >
                  <SelectTrigger class="w-24 h-8">
                    <span class="text-sm">+ Add</span>
                  </SelectTrigger>
                  <SelectContent>
                    {#each [1, 2, 5, 10, 15, 20, 30, 45, 60, 90, 120] as min}
                      <SelectItem value={min.toString()}>{min} min</SelectItem>
                    {/each}
                  </SelectContent>
                </Select>
              </div>
            </div>
          </div>

          <Separator />

          <div class="space-y-4">
            <div class="flex items-center justify-between">
              <div class="flex items-center gap-3">
                <RotateCcw class="h-5 w-5 text-muted-foreground" />
                <div>
                  <Label>Re-raise if Unacknowledged</Label>
                  <p class="text-sm text-muted-foreground">
                    Repeat alarm if not acknowledged
                  </p>
                </div>
              </div>
              <Switch bind:checked={editedProfile.reraise.enabled} />
            </div>

            {#if editedProfile.reraise.enabled}
              <div class="grid gap-4 sm:grid-cols-2 p-4 bg-muted/50 rounded-lg">
                <div class="space-y-2">
                  <Label>Re-raise every</Label>
                  <div class="flex items-center gap-2">
                    <Input
                      type="number"
                      bind:value={editedProfile.reraise.intervalMinutes}
                      class="w-20"
                      min="1"
                    />
                    <span class="text-sm text-muted-foreground">minutes</span>
                  </div>
                </div>
                <div class="flex items-center justify-between">
                  <div>
                    <Label>Escalate Volume</Label>
                    <p class="text-sm text-muted-foreground">
                      Get louder each time
                    </p>
                  </div>
                  <Switch bind:checked={editedProfile.reraise.escalate} />
                </div>
              </div>
            {/if}
          </div>

          <Separator />

          <div class="space-y-4">
            <div class="flex items-center justify-between">
              <div class="flex items-center gap-3">
                <Sparkles class="h-5 w-5 text-muted-foreground" />
                <div>
                  <Label>Smart Snooze</Label>
                  <p class="text-sm text-muted-foreground">
                    Auto-extend snooze if trending in correct direction
                  </p>
                </div>
              </div>
              <Switch bind:checked={editedProfile.smartSnooze.enabled} />
            </div>

            {#if editedProfile.smartSnooze.enabled}
              <div class="p-4 bg-muted/50 rounded-lg space-y-4">
                <p class="text-sm text-muted-foreground">
                  For high alarms: extend if glucose is falling.
                  <br />
                  For low alarms: extend if glucose is rising.
                </p>
                <div class="grid gap-4 sm:grid-cols-3">
                  <div class="space-y-2">
                    <Label class="text-sm">Min Delta</Label>
                    <div class="flex items-center gap-2">
                      <Input
                        type="number"
                        bind:value={editedProfile.smartSnooze.minDeltaThreshold}
                        class="w-20"
                        min="1"
                      />
                      <span class="text-xs text-muted-foreground">
                        mg/dL/5min
                      </span>
                    </div>
                  </div>
                  <div class="space-y-2">
                    <Label class="text-sm">Extend by</Label>
                    <div class="flex items-center gap-2">
                      <Input
                        type="number"
                        bind:value={editedProfile.smartSnooze.extensionMinutes}
                        class="w-20"
                        min="1"
                      />
                      <span class="text-sm text-muted-foreground">min</span>
                    </div>
                  </div>
                  <div class="space-y-2">
                    <Label class="text-sm">Max Total</Label>
                    <div class="flex items-center gap-2">
                      <Input
                        type="number"
                        bind:value={editedProfile.smartSnooze.maxTotalMinutes}
                        class="w-20"
                        min="1"
                      />
                      <span class="text-sm text-muted-foreground">min</span>
                    </div>
                  </div>
                </div>
              </div>
            {/if}
          </div>
        </TabsContent>

        <!-- Schedule Tab -->
        <TabsContent value="schedule" class="space-y-6 mt-6">
          <div class="flex items-center justify-between">
            <div class="flex items-center gap-3">
              <Clock class="h-5 w-5 text-muted-foreground" />
              <div>
                <Label>Time-Based Schedule</Label>
                <p class="text-sm text-muted-foreground">
                  Only activate alarm during specific times
                </p>
              </div>
            </div>
            <Switch bind:checked={editedProfile.schedule.enabled} />
          </div>

          {#if editedProfile.schedule.enabled}
            <Separator />

            <div class="space-y-4">
              <div class="space-y-2">
                <Label>Active Days</Label>
                <div class="flex gap-2">
                  {#each daysOfWeek as day}
                    {@const isActive =
                      editedProfile.schedule.activeDays?.includes(day.value) ??
                      (editedProfile.schedule.activeDays === undefined ||
                        editedProfile.schedule.activeDays.length === 0)}
                    <button
                      class="px-3 py-2 rounded-lg text-sm font-medium transition-colors
                        {isActive
                        ? 'bg-primary text-primary-foreground'
                        : 'bg-muted hover:bg-muted/80'}"
                      onclick={() => toggleDay(day.value)}
                    >
                      {day.label}
                    </button>
                  {/each}
                </div>
                <p class="text-xs text-muted-foreground">
                  Click to toggle. If none selected, alarm is active every day.
                </p>
              </div>

              <div class="space-y-2">
                <div class="flex items-center justify-between">
                  <Label>Active Time Ranges</Label>
                  <Button variant="outline" size="sm" onclick={addTimeRange}>
                    + Add Range
                  </Button>
                </div>
                <div class="space-y-2">
                  {#each editedProfile.schedule.activeRanges as range, index}
                    <div
                      class="flex items-center gap-2 p-3 bg-muted/50 rounded-lg"
                    >
                      <Input
                        type="time"
                        bind:value={range.startTime}
                        class="w-32"
                      />
                      <span class="text-muted-foreground">to</span>
                      <Input
                        type="time"
                        bind:value={range.endTime}
                        class="w-32"
                      />
                      {#if editedProfile.schedule.activeRanges.length > 1}
                        <Button
                          variant="ghost"
                          size="icon"
                          class="text-destructive"
                          onclick={() => removeTimeRange(index)}
                        >
                          ×
                        </Button>
                      {/if}
                    </div>
                  {/each}
                </div>
              </div>
            </div>
          {:else}
            <div class="text-center py-8 text-muted-foreground">
              <Clock class="h-12 w-12 mx-auto mb-4 opacity-50" />
              <p>This alarm is always active</p>
              <p class="text-sm">
                Enable scheduling to restrict when this alarm can trigger
              </p>
            </div>
          {/if}
        </TabsContent>
      </Tabs>
    </div>

    <DialogFooter class="border-t pt-4">
      <div class="flex items-center justify-between w-full">
        <div class="flex items-center gap-2">
          <Switch bind:checked={editedProfile.enabled} />
          <Label>Alarm Enabled</Label>
        </div>
        <div class="flex gap-2">
          <Button variant="outline" onclick={onCancel}>Cancel</Button>
          <Button onclick={handleSave}>Save Alarm</Button>
        </div>
      </div>
    </DialogFooter>
  </DialogContent>
</Dialog>
