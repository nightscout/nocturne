<script lang="ts">
  import type { Profile, ProfileData, TimeValue } from "$lib/api";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import * as Tabs from "$lib/components/ui/tabs";
  import * as Table from "$lib/components/ui/table";
  import * as Select from "$lib/components/ui/select";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Badge } from "$lib/components/ui/badge";
  import { BG_UNITS } from "$lib/constants/profile-icons";
  import ProfileIconPicker from "./ProfileIconPicker.svelte";
  import {
    Edit,
    Plus,
    Trash2,
    Activity,
    Droplet,
    TrendingUp,
    Target,
  } from "lucide-svelte";

  interface Props {
    open: boolean;
    profile: Profile | null;
    storeName: string | null;
    initialTab?: "general" | "basal" | "carbratio" | "sens" | "targets";
    isLoading?: boolean;
    onClose: () => void;
    onSave: (profile: Profile) => void;
  }

  let {
    open = $bindable(),
    profile,
    storeName,
    initialTab = "general",
    isLoading = false,
    onClose,
    onSave,
  }: Props = $props();

  // Deep clone the profile for editing
  let editedProfile = $state<Profile | null>(null);
  let editedStoreName = $state<string | null>(null);
  let showExternalWarning = $state(false);

  // Get the store data being edited
  let storeData = $derived.by(() => {
    if (!editedProfile?.store || !editedStoreName) return null;
    return editedProfile.store[editedStoreName] ?? null;
  });

  // Track which tab is active for time-value editing
  let activeTab = $state<
    "general" | "basal" | "carbratio" | "sens" | "targets"
  >("general");

  // Reset form when dialog opens or profile changes
  $effect(() => {
    if (open && profile) {
      // Deep clone the profile
      editedProfile = JSON.parse(JSON.stringify(profile));
      editedStoreName = storeName ?? profile.defaultProfile ?? null;
      activeTab = initialTab;

      if (profile.isExternallyManaged) {
        showExternalWarning = true;
      }
    }
  });

  function handleClose() {
    editedProfile = null;
    editedStoreName = null;
    activeTab = "general";
    onClose();
  }

  function handleSave() {
    if (!editedProfile) return;
    onSave(editedProfile);
  }

  // Time value editing helpers
  function addTimeValue(
    field: "basal" | "carbratio" | "sens" | "target_low" | "target_high"
  ) {
    if (!editedProfile?.store || !editedStoreName) return;

    const store = editedProfile.store[editedStoreName];
    if (!store) return;

    const arr = store[field] ?? [];
    arr.push({ time: "12:00", value: 0 });
    store[field] = arr;

    // Force reactivity
    editedProfile = { ...editedProfile };
  }

  function removeTimeValue(
    field: "basal" | "carbratio" | "sens" | "target_low" | "target_high",
    index: number
  ) {
    if (!editedProfile?.store || !editedStoreName) return;

    const store = editedProfile.store[editedStoreName];
    if (!store) return;

    const arr = store[field] ?? [];
    arr.splice(index, 1);
    store[field] = arr;

    // Force reactivity
    editedProfile = { ...editedProfile };
  }

  function updateTimeValue(
    field: "basal" | "carbratio" | "sens" | "target_low" | "target_high",
    index: number,
    prop: "time" | "value",
    value: string | number
  ) {
    if (!editedProfile?.store || !editedStoreName) return;

    const store = editedProfile.store[editedStoreName];
    if (!store || !store[field]?.[index]) return;

    if (prop === "time") {
      store[field]![index].time = value as string;
    } else {
      store[field]![index].value = Number(value);
    }

    // Force reactivity
    editedProfile = { ...editedProfile };
  }

  function updateStoreField(field: keyof ProfileData, value: any) {
    if (!editedProfile?.store || !editedStoreName) return;

    const store = editedProfile.store[editedStoreName];
    if (!store) return;

    (store as any)[field] = value;
    editedProfile = { ...editedProfile };
  }

  function updateProfileField(field: keyof Profile, value: any) {
    if (!editedProfile) return;
    (editedProfile as any)[field] = value;
    editedProfile = { ...editedProfile };
  }
</script>

<Dialog.Root bind:open onOpenChange={(isOpen) => !isOpen && handleClose()}>
  <Dialog.Content class="max-w-3xl max-h-[85vh] overflow-hidden flex flex-col">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <Edit class="h-5 w-5" />
        Edit Profile: {editedStoreName ?? "Unknown"}
      </Dialog.Title>
      <Dialog.Description>
        Modify your therapy settings. Changes are not saved until you click
        Save.
      </Dialog.Description>
    </Dialog.Header>

    {#if editedProfile && storeData}
      <Tabs.Root
        bind:value={activeTab}
        class="flex-1 overflow-hidden flex flex-col"
      >
        <Tabs.List class="grid w-full grid-cols-5">
          <Tabs.Trigger value="general">General</Tabs.Trigger>
          <Tabs.Trigger value="basal">Basal</Tabs.Trigger>
          <Tabs.Trigger value="carbratio">I:C Ratio</Tabs.Trigger>
          <Tabs.Trigger value="sens">ISF</Tabs.Trigger>
          <Tabs.Trigger value="targets">Targets</Tabs.Trigger>
        </Tabs.List>

        <div class="flex-1 overflow-y-auto mt-4 pr-2">
          <!-- General Settings Tab -->
          <Tabs.Content value="general" class="space-y-4 mt-0">
            <div class="grid grid-cols-2 gap-4">
              <!-- Profile Name -->
              <div class="space-y-2">
                <Label for="profile-name">Profile Name</Label>
                <Input
                  id="profile-name"
                  value={editedProfile.defaultProfile ?? ""}
                  onchange={(e) =>
                    updateProfileField("defaultProfile", e.currentTarget.value)}
                />
              </div>

              <!-- Icon -->
              <div class="space-y-2">
                <Label>Icon</Label>
                <ProfileIconPicker
                  selectedIcon={(editedProfile as any).icon ?? "user"}
                  disabled={false}
                />
              </div>

              <!-- Units -->
              <div class="space-y-2">
                <Label>Blood Glucose Units</Label>
                <Select.Root
                  type="single"
                  value={editedProfile.units ?? "mg/dL"}
                  onValueChange={(v) => {
                    updateProfileField("units", v);
                    updateStoreField("units", v);
                  }}
                >
                  <Select.Trigger class="w-full">
                    {editedProfile.units ?? "mg/dL"}
                  </Select.Trigger>
                  <Select.Content>
                    {#each BG_UNITS as unit}
                      <Select.Item value={unit.value}>{unit.label}</Select.Item>
                    {/each}
                  </Select.Content>
                </Select.Root>
              </div>

              <!-- Timezone -->
              <div class="space-y-2">
                <Label>Timezone</Label>
                <Select.Root
                  type="single"
                  value={storeData.timezone ?? ""}
                  onValueChange={(v) => updateStoreField("timezone", v)}
                >
                  <Select.Trigger class="w-full">
                    {storeData.timezone ?? "Select timezone"}
                  </Select.Trigger>
                  <Select.Content>
                    {#each Intl.DateTimeFormat().resolvedOptions().timeZone as tz}
                      <Select.Item value={tz}>{tz}</Select.Item>
                    {/each}
                  </Select.Content>
                </Select.Root>
              </div>

              <!-- DIA -->
              <div class="space-y-2">
                <Label for="dia">Duration of Insulin Action (hours)</Label>
                <Input
                  id="dia"
                  type="number"
                  step="0.5"
                  min="0"
                  max="10"
                  value={storeData.dia ?? 4}
                  onchange={(e) =>
                    updateStoreField("dia", Number(e.currentTarget.value))}
                />
              </div>

              <!-- Carbs/hr -->
              <div class="space-y-2">
                <Label for="carbs-hr">Carbs Absorption Rate (g/hr)</Label>
                <Input
                  id="carbs-hr"
                  type="number"
                  step="1"
                  min="0"
                  max="100"
                  value={storeData.carbs_hr ?? 20}
                  onchange={(e) =>
                    updateStoreField("carbs_hr", Number(e.currentTarget.value))}
                />
              </div>
            </div>
          </Tabs.Content>

          <!-- Basal Rates Tab -->
          <Tabs.Content value="basal" class="mt-0">
            {@render TimeValueEditor({
              title: "Basal Rates",
              description:
                "Background insulin delivery rates throughout the day",
              unit: "U/hr",
              icon: Activity,
              field: "basal",
              values: storeData.basal ?? [],
            })}
          </Tabs.Content>

          <!-- Carb Ratio Tab -->
          <Tabs.Content value="carbratio" class="mt-0">
            {@render TimeValueEditor({
              title: "Carb Ratios (I:C)",
              description:
                "Grams of carbohydrates covered by one unit of insulin",
              unit: "g/U",
              icon: Droplet,
              field: "carbratio",
              values: storeData.carbratio ?? [],
            })}
          </Tabs.Content>

          <!-- ISF Tab -->
          <Tabs.Content value="sens" class="mt-0">
            {@render TimeValueEditor({
              title: "Insulin Sensitivity Factor (ISF)",
              description:
                "How much your blood glucose drops per unit of insulin",
              unit: editedProfile.units === "mmol" ? "mmol/L/U" : "mg/dL/U",
              icon: TrendingUp,
              field: "sens",
              values: storeData.sens ?? [],
            })}
          </Tabs.Content>

          <!-- Targets Tab -->
          <Tabs.Content value="targets" class="mt-0 space-y-6">
            <div class="flex items-center gap-3 mb-4">
              <div
                class="flex h-10 w-10 items-center justify-center rounded-lg bg-amber-500/10"
              >
                <Target class="h-5 w-5 text-amber-600" />
              </div>
              <div>
                <h3 class="font-medium">Target Blood Glucose Range</h3>
                <p class="text-sm text-muted-foreground">
                  Set your desired low and high targets throughout the day
                </p>
              </div>
            </div>

            <div class="grid md:grid-cols-2 gap-6">
              <!-- Target Low -->
              <div class="space-y-3">
                <div class="flex items-center justify-between">
                  <Label class="text-base font-medium">Target Low</Label>
                  <Button
                    variant="outline"
                    size="sm"
                    onclick={() => addTimeValue("target_low")}
                  >
                    <Plus class="h-4 w-4 mr-1" />
                    Add
                  </Button>
                </div>
                <Table.Root>
                  <Table.Header>
                    <Table.Row>
                      <Table.Head>Time</Table.Head>
                      <Table.Head>Value</Table.Head>
                      <Table.Head class="w-10"></Table.Head>
                    </Table.Row>
                  </Table.Header>
                  <Table.Body>
                    {#each storeData.target_low ?? [] as tv, i}
                      <Table.Row>
                        <Table.Cell>
                          <Input
                            type="time"
                            value={tv.time ?? "00:00"}
                            class="w-24"
                            onchange={(e) =>
                              updateTimeValue(
                                "target_low",
                                i,
                                "time",
                                e.currentTarget.value
                              )}
                          />
                        </Table.Cell>
                        <Table.Cell>
                          <Input
                            type="number"
                            step="1"
                            value={tv.value ?? 0}
                            class="w-20"
                            onchange={(e) =>
                              updateTimeValue(
                                "target_low",
                                i,
                                "value",
                                Number(e.currentTarget.value)
                              )}
                          />
                        </Table.Cell>
                        <Table.Cell>
                          <Button
                            variant="ghost"
                            size="icon"
                            class="h-8 w-8 text-destructive"
                            onclick={() => removeTimeValue("target_low", i)}
                          >
                            <Trash2 class="h-4 w-4" />
                          </Button>
                        </Table.Cell>
                      </Table.Row>
                    {/each}
                  </Table.Body>
                </Table.Root>
              </div>

              <!-- Target High -->
              <div class="space-y-3">
                <div class="flex items-center justify-between">
                  <Label class="text-base font-medium">Target High</Label>
                  <Button
                    variant="outline"
                    size="sm"
                    onclick={() => addTimeValue("target_high")}
                  >
                    <Plus class="h-4 w-4 mr-1" />
                    Add
                  </Button>
                </div>
                <Table.Root>
                  <Table.Header>
                    <Table.Row>
                      <Table.Head>Time</Table.Head>
                      <Table.Head>Value</Table.Head>
                      <Table.Head class="w-10"></Table.Head>
                    </Table.Row>
                  </Table.Header>
                  <Table.Body>
                    {#each storeData.target_high ?? [] as tv, i}
                      <Table.Row>
                        <Table.Cell>
                          <Input
                            type="time"
                            value={tv.time ?? "00:00"}
                            class="w-24"
                            onchange={(e) =>
                              updateTimeValue(
                                "target_high",
                                i,
                                "time",
                                e.currentTarget.value
                              )}
                          />
                        </Table.Cell>
                        <Table.Cell>
                          <Input
                            type="number"
                            step="1"
                            value={tv.value ?? 0}
                            class="w-20"
                            onchange={(e) =>
                              updateTimeValue(
                                "target_high",
                                i,
                                "value",
                                Number(e.currentTarget.value)
                              )}
                          />
                        </Table.Cell>
                        <Table.Cell>
                          <Button
                            variant="ghost"
                            size="icon"
                            class="h-8 w-8 text-destructive"
                            onclick={() => removeTimeValue("target_high", i)}
                          >
                            <Trash2 class="h-4 w-4" />
                          </Button>
                        </Table.Cell>
                      </Table.Row>
                    {/each}
                  </Table.Body>
                </Table.Root>
              </div>
            </div>
          </Tabs.Content>
        </div>
      </Tabs.Root>

      <Dialog.Footer class="pt-4 border-t mt-4">
        <div
          class="flex items-center gap-2 text-sm text-muted-foreground mr-auto"
        >
          <Badge variant="secondary">Unsaved changes</Badge>
        </div>
        <Button variant="outline" onclick={handleClose} disabled={isLoading}>
          Cancel
        </Button>
        <Button onclick={handleSave} disabled={isLoading}>
          {#if isLoading}
            Saving...
          {:else}
            Save Changes
          {/if}
        </Button>
      </Dialog.Footer>
    {:else}
      <div class="py-8 text-center text-muted-foreground">
        No profile selected for editing.
      </div>
    {/if}
  </Dialog.Content>
</Dialog.Root>

<AlertDialog.Root bind:open={showExternalWarning}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Externally Managed Profile</AlertDialog.Title>
      <AlertDialog.Description>
        This profile is managed by an external source (e.g., Glooko). <br />
        Any changes made here will
        <strong>NOT</strong>
        be reflected on your device (pump/CGM). They will only affect how data is
        viewed in Nocturne.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Action>I Understand</AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<!-- Time Value Editor Snippet -->
{#snippet TimeValueEditor({
  title,
  description,
  unit,
  icon: Icon,
  field,
  values,
}: {
  title: string;
  description: string;
  unit: string;
  icon: typeof Activity;
  field: "basal" | "carbratio" | "sens";
  values: TimeValue[];
})}
  <div class="space-y-4">
    <div class="flex items-center justify-between">
      <div class="flex items-center gap-3">
        <div
          class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
        >
          <Icon class="h-5 w-5 text-primary" />
        </div>
        <div>
          <h3 class="font-medium">{title}</h3>
          <p class="text-sm text-muted-foreground">{description}</p>
        </div>
      </div>
      <Button variant="outline" size="sm" onclick={() => addTimeValue(field)}>
        <Plus class="h-4 w-4 mr-1" />
        Add Time Block
      </Button>
    </div>

    <Table.Root>
      <Table.Header>
        <Table.Row>
          <Table.Head>Start Time</Table.Head>
          <Table.Head class="text-right">{unit}</Table.Head>
          <Table.Head class="w-10"></Table.Head>
        </Table.Row>
      </Table.Header>
      <Table.Body>
        {#each values as tv, i}
          <Table.Row>
            <Table.Cell>
              <Input
                type="time"
                value={tv.time ?? "00:00"}
                class="w-28"
                onchange={(e) =>
                  updateTimeValue(field, i, "time", e.currentTarget.value)}
              />
            </Table.Cell>
            <Table.Cell class="text-right">
              <Input
                type="number"
                step="0.1"
                value={tv.value ?? 0}
                class="ml-auto w-24 text-right"
                onchange={(e) =>
                  updateTimeValue(
                    field,
                    i,
                    "value",
                    Number(e.currentTarget.value)
                  )}
              />
            </Table.Cell>
            <Table.Cell>
              <Button
                variant="ghost"
                size="icon"
                class="h-8 w-8 text-destructive hover:text-destructive"
                onclick={() => removeTimeValue(field, i)}
              >
                <Trash2 class="h-4 w-4" />
              </Button>
            </Table.Cell>
          </Table.Row>
        {:else}
          <Table.Row>
            <Table.Cell
              colspan={3}
              class="text-center py-4 text-muted-foreground"
            >
              No time blocks configured. Click "Add Time Block" to get started.
            </Table.Cell>
          </Table.Row>
        {/each}
      </Table.Body>
    </Table.Root>
  </div>
{/snippet}
