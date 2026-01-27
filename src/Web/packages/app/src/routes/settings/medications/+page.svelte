<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Select from "$lib/components/ui/select";
  import { Switch } from "$lib/components/ui/switch";
  import type {
    InjectableMedication,
    InjectableCategory,
    UnitType,
  } from "$lib/api";
  import {
    Pill,
    Plus,
    Edit,
    Archive,
    Clock,
    Droplets,
    Syringe,
    AlertCircle,
  } from "lucide-svelte";
  import {
    getMedications,
    createMedication,
    updateMedication,
    archiveMedication,
  } from "./data.remote";

  // State
  let showArchived = $state(false);
  let showCreateDialog = $state(false);
  let showEditDialog = $state(false);
  let editingMedication = $state<InjectableMedication | null>(null);
  let isLoading = $state(false);

  // Query for medications
  const medicationsQuery = $derived(getMedications(showArchived));

  // Category display names
  const categoryLabels: Record<string, string> = {
    RapidActing: "Rapid-Acting",
    UltraRapid: "Ultra-Rapid",
    ShortActing: "Short-Acting",
    Intermediate: "Intermediate",
    LongActing: "Long-Acting",
    UltraLong: "Ultra-Long",
    GLP1Daily: "GLP-1 Daily",
    GLP1Weekly: "GLP-1 Weekly",
    Other: "Other",
  };

  const categoryOptions = [
    { value: "RapidActing", label: "Rapid-Acting" },
    { value: "UltraRapid", label: "Ultra-Rapid" },
    { value: "ShortActing", label: "Short-Acting" },
    { value: "Intermediate", label: "Intermediate" },
    { value: "LongActing", label: "Long-Acting" },
    { value: "UltraLong", label: "Ultra-Long" },
    { value: "GLP1Daily", label: "GLP-1 Daily" },
    { value: "GLP1Weekly", label: "GLP-1 Weekly" },
    { value: "Other", label: "Other" },
  ];

  const unitTypeOptions = [
    { value: "Units", label: "Units (U)" },
    { value: "Milligrams", label: "Milligrams (mg)" },
  ];

  // Category color mapping
  function getCategoryColor(category: string | undefined): string {
    switch (category) {
      case "RapidActing":
      case "UltraRapid":
        return "bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-100";
      case "ShortActing":
        return "bg-cyan-100 text-cyan-800 dark:bg-cyan-900 dark:text-cyan-100";
      case "Intermediate":
        return "bg-amber-100 text-amber-800 dark:bg-amber-900 dark:text-amber-100";
      case "LongActing":
      case "UltraLong":
        return "bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100";
      case "GLP1Daily":
      case "GLP1Weekly":
        return "bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-100";
      default:
        return "bg-gray-100 text-gray-800 dark:bg-gray-900 dark:text-gray-100";
    }
  }

  // Group medications by category
  function groupByCategory(
    medications: InjectableMedication[]
  ): Record<string, InjectableMedication[]> {
    const groups: Record<string, InjectableMedication[]> = {};
    for (const med of medications) {
      const cat = med.category ?? "Other";
      if (!groups[cat]) groups[cat] = [];
      groups[cat].push(med);
    }
    // Sort within each group by sortOrder then name
    for (const cat of Object.keys(groups)) {
      groups[cat].sort((a, b) => {
        const orderDiff = (a.sortOrder ?? 0) - (b.sortOrder ?? 0);
        if (orderDiff !== 0) return orderDiff;
        return (a.name ?? "").localeCompare(b.name ?? "");
      });
    }
    return groups;
  }

  // Category display order
  const categoryOrder = [
    "RapidActing",
    "UltraRapid",
    "ShortActing",
    "Intermediate",
    "LongActing",
    "UltraLong",
    "GLP1Daily",
    "GLP1Weekly",
    "Other",
  ];

  // Form state for create/edit
  let formName = $state("");
  let formCategory = $state("RapidActing");
  let formConcentration = $state<number | undefined>(undefined);
  let formUnitType = $state("Units");
  let formDia = $state<number | undefined>(undefined);
  let formOnset = $state<number | undefined>(undefined);
  let formPeak = $state<number | undefined>(undefined);
  let formDuration = $state<number | undefined>(undefined);
  let formDefaultDose = $state<number | undefined>(undefined);

  function resetForm() {
    formName = "";
    formCategory = "RapidActing";
    formConcentration = undefined;
    formUnitType = "Units";
    formDia = undefined;
    formOnset = undefined;
    formPeak = undefined;
    formDuration = undefined;
    formDefaultDose = undefined;
  }

  function populateForm(med: InjectableMedication) {
    formName = med.name ?? "";
    formCategory = med.category ?? "RapidActing";
    formConcentration = med.concentration;
    formUnitType = med.unitType ?? "Units";
    formDia = med.dia ?? undefined;
    formOnset = med.onset ?? undefined;
    formPeak = med.peak ?? undefined;
    formDuration = med.duration ?? undefined;
    formDefaultDose = med.defaultDose ?? undefined;
  }

  function openCreateDialog() {
    resetForm();
    showCreateDialog = true;
  }

  function openEditDialog(med: InjectableMedication) {
    editingMedication = med;
    populateForm(med);
    showEditDialog = true;
  }

  async function handleCreate() {
    if (!formName.trim()) return;
    isLoading = true;
    try {
      await createMedication({
        name: formName.trim(),
        category: formCategory,
        concentration: formConcentration,
        unitType: formUnitType,
        dia: formDia,
        onset: formOnset,
        peak: formPeak,
        duration: formDuration,
        defaultDose: formDefaultDose,
      });
      showCreateDialog = false;
      resetForm();
    } catch (err) {
      console.error("Error creating medication:", err);
    } finally {
      isLoading = false;
    }
  }

  async function handleUpdate() {
    if (!editingMedication?.id || !formName.trim()) return;
    isLoading = true;
    try {
      const updated: InjectableMedication = {
        ...editingMedication,
        name: formName.trim(),
        category: formCategory as InjectableCategory,
        concentration: formConcentration,
        unitType: formUnitType as UnitType,
        dia: formDia,
        onset: formOnset,
        peak: formPeak,
        duration: formDuration,
        defaultDose: formDefaultDose,
      };
      await updateMedication({
        id: editingMedication.id,
        medication: updated,
      });
      showEditDialog = false;
      editingMedication = null;
      resetForm();
    } catch (err) {
      console.error("Error updating medication:", err);
    } finally {
      isLoading = false;
    }
  }

  async function handleArchive(med: InjectableMedication) {
    if (!med.id) return;
    isLoading = true;
    try {
      await archiveMedication(med.id);
    } catch (err) {
      console.error("Error archiving medication:", err);
    } finally {
      isLoading = false;
    }
  }

  function formatDuration(hours: number | undefined): string {
    if (hours === undefined || hours === null) return "--";
    if (hours < 1) return `${Math.round(hours * 60)}min`;
    return `${hours}h`;
  }
</script>

<svelte:head>
  <title>Medications - Nocturne</title>
  <meta
    name="description"
    content="Manage your injectable medications catalog"
  />
</svelte:head>

{#await medicationsQuery}
  <div class="container mx-auto p-6 max-w-5xl">
    <div class="flex items-center justify-center h-64">
      <div class="animate-pulse text-muted-foreground">
        Loading medications...
      </div>
    </div>
  </div>
{:then medications}
  {@const grouped = groupByCategory(medications)}
  <div class="container mx-auto p-6 max-w-5xl space-y-6">
    <!-- Header -->
    <div class="flex items-start justify-between">
      <div class="flex items-center gap-3">
        <div
          class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10"
        >
          <Pill class="h-6 w-6 text-primary" />
        </div>
        <div>
          <h1 class="text-3xl font-bold tracking-tight">Medications</h1>
          <p class="text-muted-foreground">
            Manage your injectable medications catalog
          </p>
        </div>
      </div>
      <div class="flex items-center gap-3">
        <div class="flex items-center gap-2">
          <Label for="show-archived" class="text-sm text-muted-foreground">
            Show archived
          </Label>
          <Switch
            id="show-archived"
            checked={showArchived}
            onCheckedChange={(checked) => (showArchived = checked)}
          />
        </div>
        <Button onclick={openCreateDialog}>
          <Plus class="h-4 w-4 mr-2" />
          Add Medication
        </Button>
      </div>
    </div>

    {#if medications.length === 0}
      <!-- Empty State -->
      <Card class="border-dashed">
        <CardContent class="py-12">
          <div class="text-center space-y-4">
            <div
              class="mx-auto flex h-16 w-16 items-center justify-center rounded-full bg-muted"
            >
              <Pill class="h-8 w-8 text-muted-foreground" />
            </div>
            <div>
              <h3 class="text-lg font-semibold">No Medications Found</h3>
              <p class="text-sm text-muted-foreground max-w-md mx-auto mt-1">
                Add your injectable medications to track doses and manage your
                insulin therapy.
              </p>
            </div>
            <Button onclick={openCreateDialog}>
              <Plus class="h-4 w-4 mr-2" />
              Add Your First Medication
            </Button>
          </div>
        </CardContent>
      </Card>
    {:else}
      <!-- Medications grouped by category -->
      {#each categoryOrder as category}
        {#if grouped[category] && grouped[category].length > 0}
          <Card>
            <CardHeader class="pb-3">
              <div class="flex items-center gap-2">
                <Badge class={getCategoryColor(category)}>
                  {categoryLabels[category] ?? category}
                </Badge>
                <CardDescription>
                  {grouped[category].length} medication{grouped[category]
                    .length !== 1
                    ? "s"
                    : ""}
                </CardDescription>
              </div>
            </CardHeader>
            <CardContent>
              <div class="space-y-3">
                {#each grouped[category] as med}
                  <div
                    class="flex items-center justify-between p-3 rounded-lg border {med.isArchived
                      ? 'opacity-50 bg-muted/30'
                      : 'hover:bg-accent/50'} transition-colors"
                  >
                    <div class="flex items-center gap-4 flex-1 min-w-0">
                      <div
                        class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
                      >
                        <Syringe class="h-5 w-5 text-primary" />
                      </div>
                      <div class="flex-1 min-w-0">
                        <div class="flex items-center gap-2">
                          <span class="font-medium truncate">
                            {med.name ?? "Unnamed"}
                          </span>
                          {#if med.isArchived}
                            <Badge variant="outline" class="text-xs">
                              Archived
                            </Badge>
                          {/if}
                        </div>
                        <div
                          class="flex items-center gap-3 text-xs text-muted-foreground mt-0.5"
                        >
                          {#if med.concentration}
                            <span class="flex items-center gap-1">
                              <Droplets class="h-3 w-3" />
                              {med.concentration}
                              {med.unitType === "Milligrams" ? "mg" : "U"}/mL
                            </span>
                          {/if}
                          {#if med.dia !== undefined && med.dia !== null}
                            <span class="flex items-center gap-1">
                              <Clock class="h-3 w-3" />
                              DIA {formatDuration(med.dia)}
                            </span>
                          {/if}
                          {#if med.duration !== undefined && med.duration !== null}
                            <span class="flex items-center gap-1">
                              <Clock class="h-3 w-3" />
                              Duration {formatDuration(med.duration)}
                            </span>
                          {/if}
                          {#if med.defaultDose !== undefined && med.defaultDose !== null}
                            <span>
                              Default: {med.defaultDose}{med.unitType ===
                              "Milligrams"
                                ? "mg"
                                : "U"}
                            </span>
                          {/if}
                        </div>
                      </div>
                    </div>
                    <div class="flex items-center gap-1 shrink-0">
                      <Button
                        variant="ghost"
                        size="icon"
                        class="h-8 w-8"
                        onclick={() => openEditDialog(med)}
                      >
                        <Edit class="h-4 w-4" />
                      </Button>
                      {#if !med.isArchived}
                        <Button
                          variant="ghost"
                          size="icon"
                          class="h-8 w-8 text-muted-foreground hover:text-destructive"
                          onclick={() => handleArchive(med)}
                        >
                          <Archive class="h-4 w-4" />
                        </Button>
                      {/if}
                    </div>
                  </div>
                {/each}
              </div>
            </CardContent>
          </Card>
        {/if}
      {/each}
    {/if}
  </div>
{:catch err}
  <div class="container mx-auto p-6 max-w-5xl">
    <Card class="border-destructive">
      <CardContent class="py-8">
        <div class="text-center space-y-2">
          <AlertCircle class="h-8 w-8 text-destructive mx-auto" />
          <p class="text-destructive font-medium">
            Failed to load medications
          </p>
          <p class="text-sm text-muted-foreground">
            {err instanceof Error ? err.message : "An error occurred"}
          </p>
          <Button variant="outline" onclick={() => window.location.reload()}>
            Try again
          </Button>
        </div>
      </CardContent>
    </Card>
  </div>
{/await}

<!-- Create Medication Dialog -->
<Dialog.Root bind:open={showCreateDialog}>
  <Dialog.Content class="sm:max-w-lg">
    <Dialog.Header>
      <Dialog.Title>Add Medication</Dialog.Title>
      <Dialog.Description>
        Add a new injectable medication to your catalog.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="create-name">Name</Label>
        <Input
          id="create-name"
          placeholder="e.g. Humalog, Tresiba, Ozempic"
          bind:value={formName}
        />
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label>Category</Label>
          <Select.Root
            type="single"
            value={formCategory}
            onValueChange={(v) => {
              if (v) formCategory = v;
            }}
          >
            <Select.Trigger class="w-full">
              {categoryLabels[formCategory] ?? formCategory}
            </Select.Trigger>
            <Select.Content>
              {#each categoryOptions as opt}
                <Select.Item value={opt.value}>{opt.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
        <div class="space-y-2">
          <Label>Unit Type</Label>
          <Select.Root
            type="single"
            value={formUnitType}
            onValueChange={(v) => {
              if (v) formUnitType = v;
            }}
          >
            <Select.Trigger class="w-full">
              {unitTypeOptions.find((o) => o.value === formUnitType)?.label ??
                formUnitType}
            </Select.Trigger>
            <Select.Content>
              {#each unitTypeOptions as opt}
                <Select.Item value={opt.value}>{opt.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label for="create-concentration">Concentration (U/mL)</Label>
          <Input
            id="create-concentration"
            type="number"
            placeholder="e.g. 100"
            bind:value={formConcentration}
          />
        </div>
        <div class="space-y-2">
          <Label for="create-default-dose">Default Dose</Label>
          <Input
            id="create-default-dose"
            type="number"
            placeholder="e.g. 10"
            bind:value={formDefaultDose}
          />
        </div>
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label for="create-dia">DIA (hours)</Label>
          <Input
            id="create-dia"
            type="number"
            step="0.5"
            placeholder="e.g. 4"
            bind:value={formDia}
          />
        </div>
        <div class="space-y-2">
          <Label for="create-duration">Duration (hours)</Label>
          <Input
            id="create-duration"
            type="number"
            step="0.5"
            placeholder="e.g. 24"
            bind:value={formDuration}
          />
        </div>
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label for="create-onset">Onset (hours)</Label>
          <Input
            id="create-onset"
            type="number"
            step="0.25"
            placeholder="e.g. 0.25"
            bind:value={formOnset}
          />
        </div>
        <div class="space-y-2">
          <Label for="create-peak">Peak (hours)</Label>
          <Input
            id="create-peak"
            type="number"
            step="0.25"
            placeholder="e.g. 1.5"
            bind:value={formPeak}
          />
        </div>
      </div>
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (showCreateDialog = false)}>
        Cancel
      </Button>
      <Button onclick={handleCreate} disabled={isLoading || !formName.trim()}>
        {isLoading ? "Creating..." : "Add Medication"}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Edit Medication Dialog -->
<Dialog.Root bind:open={showEditDialog}>
  <Dialog.Content class="sm:max-w-lg">
    <Dialog.Header>
      <Dialog.Title>Edit Medication</Dialog.Title>
      <Dialog.Description>
        Update the settings for this medication.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="edit-name">Name</Label>
        <Input id="edit-name" bind:value={formName} />
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label>Category</Label>
          <Select.Root
            type="single"
            value={formCategory}
            onValueChange={(v) => {
              if (v) formCategory = v;
            }}
          >
            <Select.Trigger class="w-full">
              {categoryLabels[formCategory] ?? formCategory}
            </Select.Trigger>
            <Select.Content>
              {#each categoryOptions as opt}
                <Select.Item value={opt.value}>{opt.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
        <div class="space-y-2">
          <Label>Unit Type</Label>
          <Select.Root
            type="single"
            value={formUnitType}
            onValueChange={(v) => {
              if (v) formUnitType = v;
            }}
          >
            <Select.Trigger class="w-full">
              {unitTypeOptions.find((o) => o.value === formUnitType)?.label ??
                formUnitType}
            </Select.Trigger>
            <Select.Content>
              {#each unitTypeOptions as opt}
                <Select.Item value={opt.value}>{opt.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label for="edit-concentration">Concentration (U/mL)</Label>
          <Input
            id="edit-concentration"
            type="number"
            bind:value={formConcentration}
          />
        </div>
        <div class="space-y-2">
          <Label for="edit-default-dose">Default Dose</Label>
          <Input
            id="edit-default-dose"
            type="number"
            bind:value={formDefaultDose}
          />
        </div>
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label for="edit-dia">DIA (hours)</Label>
          <Input
            id="edit-dia"
            type="number"
            step="0.5"
            bind:value={formDia}
          />
        </div>
        <div class="space-y-2">
          <Label for="edit-duration">Duration (hours)</Label>
          <Input
            id="edit-duration"
            type="number"
            step="0.5"
            bind:value={formDuration}
          />
        </div>
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label for="edit-onset">Onset (hours)</Label>
          <Input
            id="edit-onset"
            type="number"
            step="0.25"
            bind:value={formOnset}
          />
        </div>
        <div class="space-y-2">
          <Label for="edit-peak">Peak (hours)</Label>
          <Input
            id="edit-peak"
            type="number"
            step="0.25"
            bind:value={formPeak}
          />
        </div>
      </div>
    </div>
    <Dialog.Footer>
      <Button
        variant="outline"
        onclick={() => {
          showEditDialog = false;
          editingMedication = null;
        }}
      >
        Cancel
      </Button>
      <Button onclick={handleUpdate} disabled={isLoading || !formName.trim()}>
        {isLoading ? "Saving..." : "Save Changes"}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
