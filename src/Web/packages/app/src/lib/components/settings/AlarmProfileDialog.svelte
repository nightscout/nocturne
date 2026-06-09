<script lang="ts">
  import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
  } from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import {
    Tabs,
    TabsContent,
    TabsList,
    TabsTrigger,
  } from "$lib/components/ui/tabs";
  import {
    Bell,
    Volume2,
    Eye,
    Clock,
    Settings2,
    Timer,
  } from "lucide-svelte";
  import type {
    AlarmProfileConfiguration,
    EmergencyContactConfig,
  } from "$lib/types/alarm-profile";
  import { normalizeAlarmType } from "$lib/types/alarm-profile";
  import AlarmGeneralTab from "./alarm-profile/AlarmGeneralTab.svelte";
  import AlarmAudioTab from "./alarm-profile/AlarmAudioTab.svelte";
  import AlarmVisualTab from "./alarm-profile/AlarmVisualTab.svelte";
  import AlarmSnoozeTab from "./alarm-profile/AlarmSnoozeTab.svelte";
  import AlarmScheduleTab from "./alarm-profile/AlarmScheduleTab.svelte";

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

  // Track previous state to avoid unnecessary re-runs
  let lastOpenState = false;
  let lastProfileId = "";

  // Handle dialog open - reload sounds and reset profile
  $effect(() => {
    const isOpen = open;
    const profileId = profile.id;

    // Only run when dialog is opening (not on every render)
    if (isOpen && !lastOpenState) {
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

  function handleSave() {
    editedProfile.updatedAt = new Date().toISOString();
    onSave(editedProfile);
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

        <TabsContent value="general" class="mt-6">
          <AlarmGeneralTab bind:profile={editedProfile} />
        </TabsContent>

        <TabsContent value="audio" class="mt-6">
          <AlarmAudioTab
            bind:profile={editedProfile}
            {emergencyContacts}
            isDialogOpen={open}
          />
        </TabsContent>

        <TabsContent value="visual" class="mt-6">
          <AlarmVisualTab
            bind:profile={editedProfile}
            {emergencyContacts}
            isDialogOpen={open}
          />
        </TabsContent>

        <TabsContent value="snooze" class="mt-6">
          <AlarmSnoozeTab bind:profile={editedProfile} />
        </TabsContent>

        <TabsContent value="schedule" class="mt-6">
          <AlarmScheduleTab bind:profile={editedProfile} />
        </TabsContent>
      </Tabs>
    </div>

    <DialogFooter class="border-t pt-4 @container">
      <div
        class="flex flex-col gap-3 @lg:flex-row @lg:items-center @lg:justify-between w-full"
      >
        <div class="flex items-center gap-2">
          <Switch bind:checked={editedProfile.enabled} />
          <Label>Alarm Enabled</Label>
        </div>
        <div class="flex flex-wrap gap-2">
          <Button variant="outline" onclick={onCancel}>Cancel</Button>
          <Button onclick={handleSave}>Save Alarm</Button>
        </div>
      </div>
    </DialogFooter>
  </DialogContent>
</Dialog>
