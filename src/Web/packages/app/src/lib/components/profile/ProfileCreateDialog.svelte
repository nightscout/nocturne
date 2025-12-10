<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import { cn } from "$lib/utils";
  import { BG_UNITS, DEFAULT_PROFILE_ICON } from "$lib/constants/profile-icons";
  import ProfileIconPicker from "./ProfileIconPicker.svelte";
  import { Plus } from "lucide-svelte";

  interface Props {
    open: boolean;
    isLoading?: boolean;
    onClose: () => void;
    onSave: (profileData: CreateProfileData) => void;
  }

  export interface CreateProfileData {
    defaultProfile: string;
    units: string;
    icon: string;
    timezone: string;
    dia: number;
    carbs_hr: number;
  }

  let {
    open = $bindable(),
    isLoading = false,
    onClose,
    onSave,
  }: Props = $props();

  // Form state
  let formState = $state<CreateProfileData>({
    defaultProfile: "",
    units: "mg/dL",
    icon: DEFAULT_PROFILE_ICON,
    timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    dia: 4,
    carbs_hr: 20,
  });

  // Validation
  let errors = $state<Record<string, string>>({});

  function validate(): boolean {
    const newErrors: Record<string, string> = {};

    if (!formState.defaultProfile.trim()) {
      newErrors.defaultProfile = "Profile name is required";
    }

    if (formState.dia <= 0 || formState.dia > 10) {
      newErrors.dia = "DIA must be between 0 and 10 hours";
    }

    if (formState.carbs_hr <= 0 || formState.carbs_hr > 100) {
      newErrors.carbs_hr = "Carbs/hr must be between 0 and 100";
    }

    errors = newErrors;
    return Object.keys(newErrors).length === 0;
  }

  function handleSubmit() {
    if (!validate()) return;
    onSave(formState);
  }

  function handleClose() {
    // Reset form on close
    formState = {
      defaultProfile: "",
      units: "mg/dL",
      icon: DEFAULT_PROFILE_ICON,
      timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      dia: 4,
      carbs_hr: 20,
    };
    errors = {};
    onClose();
  }

  // Reset form when dialog opens
  $effect(() => {
    if (open) {
      formState = {
        defaultProfile: "",
        units: "mg/dL",
        icon: DEFAULT_PROFILE_ICON,
        timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
        dia: 4,
        carbs_hr: 20,
      };
      errors = {};
    }
  });
</script>

<Dialog.Root bind:open onOpenChange={(isOpen) => !isOpen && handleClose()}>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <Plus class="h-5 w-5" />
        Create New Profile
      </Dialog.Title>
      <Dialog.Description>
        Create a new therapy profile with your insulin settings.
      </Dialog.Description>
    </Dialog.Header>

    <form
      onsubmit={(e) => {
        e.preventDefault();
        handleSubmit();
      }}
      class="space-y-4"
    >
      <!-- Profile Name -->
      <div class="space-y-2">
        <Label for="profile-name">Profile Name *</Label>
        <Input
          id="profile-name"
          bind:value={formState.defaultProfile}
          placeholder="e.g., Weekday, Weekend, Exercise"
          class={cn(errors.defaultProfile && "border-destructive")}
        />
        {#if errors.defaultProfile}
          <p class="text-sm text-destructive">{errors.defaultProfile}</p>
        {/if}
      </div>

      <!-- Icon Selection -->
      <div class="space-y-2">
        <Label>Profile Icon</Label>
        <ProfileIconPicker bind:selectedIcon={formState.icon} />
      </div>

      <!-- Units -->
      <div class="space-y-2">
        <Label for="units">Blood Glucose Units</Label>
        <Select.Root type="single" bind:value={formState.units}>
          <Select.Trigger class="w-full">
            {BG_UNITS.find((u) => u.value === formState.units)?.label ??
              "Select units"}
          </Select.Trigger>
          <Select.Content>
            {#each BG_UNITS as unit}
              <Select.Item value={unit.value}>
                {unit.label}
                <span class="text-muted-foreground text-xs">
                  ({unit.description})
                </span>
              </Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
      </div>

      <!-- Timezone -->
      <div class="space-y-2">
        <Label for="timezone">Timezone</Label>
        <Select.Root type="single" bind:value={formState.timezone}>
          <Select.Trigger class="w-full">
            {formState.timezone}
          </Select.Trigger>
          <Select.Content>
            {#each Intl.DateTimeFormat().resolvedOptions().timeZone as tz}
              <Select.Item value={tz}>{tz}</Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
      </div>

      <!-- DIA and Carbs/hr in a grid -->
      <div class="grid grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label for="dia">DIA (hours)</Label>
          <Input
            id="dia"
            type="number"
            step="0.5"
            min="0"
            max="10"
            bind:value={formState.dia}
            class={cn(errors.dia && "border-destructive")}
          />
          {#if errors.dia}
            <p class="text-xs text-destructive">{errors.dia}</p>
          {/if}
        </div>

        <div class="space-y-2">
          <Label for="carbs-hr">Carbs/hr (g/hr)</Label>
          <Input
            id="carbs-hr"
            type="number"
            step="1"
            min="0"
            max="100"
            bind:value={formState.carbs_hr}
            class={cn(errors.carbs_hr && "border-destructive")}
          />
          {#if errors.carbs_hr}
            <p class="text-xs text-destructive">{errors.carbs_hr}</p>
          {/if}
        </div>
      </div>

      <Dialog.Footer class="pt-4">
        <Button
          variant="outline"
          type="button"
          onclick={handleClose}
          disabled={isLoading}
        >
          Cancel
        </Button>
        <Button type="submit" disabled={isLoading}>
          {#if isLoading}
            Creating...
          {:else}
            Create Profile
          {/if}
        </Button>
      </Dialog.Footer>
    </form>
  </Dialog.Content>
</Dialog.Root>
