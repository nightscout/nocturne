<script lang="ts">
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import * as Tabs from "$lib/components/ui/tabs";
  import * as Table from "$lib/components/ui/table";
  import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
  import {
    ProfileCreateDialog,
    ProfileDeleteDialog,
    ProfileEditDialog,
  } from "$lib/components/profile";
  import type { CreateProfileData } from "$lib/components/profile/ProfileCreateDialog.svelte";
  import {
    User,
    UserCircle,
    Heart,
    HeartPulse,
    Activity,
    Syringe,
    Pill,
    Droplet,
    Target,
    Sun,
    Moon,
    Sunrise,
    Sunset,
    Dumbbell,
    Bike,
    Footprints,
    Utensils,
    Coffee,
    Cake,
    Baby,
    Briefcase,
    Home,
    Plane,
    Zap,
    Shield,
    Star,
    Sparkles,
    Clock,
    Calendar,
    TrendingUp,
    History,
    ChevronRight,
    Settings,
    Plus,
    Trash2,
    Edit,
    MoreVertical,
    type Icon,
  } from "lucide-svelte";
  import type { Profile, TimeValue } from "$lib/api";
  import { formatDateDetailed, bgLabel } from "$lib/utils/formatting";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import {
    getProfiles,
    createProfile,
    updateProfile,
    deleteProfile,
  } from "./data.remote";

  const MGDL_TO_MMOL = 18.01559;

  function convertValue(
    value: number | undefined,
    fromUnits: string | undefined,
    toUnits: string
  ): number | undefined {
    if (value === undefined || value === null) return undefined;

    const from = fromUnits === "mmol" ? "mmol" : "mg/dl";
    const to = toUnits === "mmol" ? "mmol" : "mg/dl";

    if (from === to) {
      return from === "mmol" ? Math.round(value * 10) / 10 : Math.round(value);
    }

    if (from === "mg/dl" && to === "mmol") {
      return Math.round((value / MGDL_TO_MMOL) * 10) / 10;
    }

    if (from === "mmol" && to === "mg/dl") {
      return Math.round(value * MGDL_TO_MMOL);
    }

    return value;
  }

  // Get the selected profile ID from URL
  const urlProfileId = $derived(page.url.searchParams.get("id"));

  // Query for profiles data - passes selectedProfileId as argument
  const profilesQuery = $derived(getProfiles(urlProfileId ?? undefined));

  // Icon component map
  const iconComponents: Record<string, typeof Icon> = {
    user: User,
    "user-circle": UserCircle,
    heart: Heart,
    "heart-pulse": HeartPulse,
    activity: Activity,
    syringe: Syringe,
    pill: Pill,
    droplet: Droplet,
    target: Target,
    sun: Sun,
    moon: Moon,
    sunrise: Sunrise,
    sunset: Sunset,
    dumbbell: Dumbbell,
    bike: Bike,
    footprints: Footprints,
    utensils: Utensils,
    coffee: Coffee,
    cake: Cake,
    baby: Baby,
    briefcase: Briefcase,
    home: Home,
    plane: Plane,
    zap: Zap,
    shield: Shield,
    star: Star,
    sparkles: Sparkles,
    clock: Clock,
    calendar: Calendar,
    "trending-up": TrendingUp,
  };

  function getProfileIcon(profile: Profile): typeof Icon {
    const iconId = (profile as any).icon ?? "user";
    return iconComponents[iconId] ?? User;
  }

  // Currently selected profile ID - derived from URL or default
  let selectedProfileId = $derived.by(() => {
    const urlId = page.url.searchParams.get("id");
    const currentData = profilesQuery.current;
    return (
      urlId ??
      currentData?.selectedProfileId ??
      currentData?.currentProfile?._id ??
      null
    );
  });

  // Derived: get selected profile
  let selectedProfile = $derived.by(() => {
    const currentData = profilesQuery.current;
    if (!currentData) return null;
    return (
      currentData.profiles.find((p: Profile) => p._id === selectedProfileId) ??
      currentData.currentProfile
    );
  });

  // Derived: get selected profile's store names
  let profileStoreNames = $derived(
    selectedProfile?.store ? Object.keys(selectedProfile.store).map(String) : []
  );

  // Derived: get default store name and data
  let defaultStoreName = $derived(selectedProfile?.defaultProfile ?? "");
  let defaultStore = $derived(
    defaultStoreName && selectedProfile?.store
      ? selectedProfile.store[defaultStoreName]
      : null
  );

  // Dialog states
  let showCreateDialog = $state(false);
  let showEditDialog = $state(false);
  let showDeleteDialog = $state(false);
  let profileToDelete = $state<Profile | null>(null);
  let editStoreName = $state<string | null>(null);
  let editInitialTab = $state<"general" | "basal" | "carbratio" | "sens" | "targets">("general");
  let isLoading = $state(false);

  function selectProfile(profileId: string | null) {
    // Update URL without full navigation
    const url = new URL(window.location.href);
    if (profileId) {
      url.searchParams.set("id", profileId);
    } else {
      url.searchParams.delete("id");
    }
    goto(url.toString(), { replaceState: true, noScroll: true });
  }

  function formatRelativeTime(dateString: string | undefined): string {
    if (!dateString) return "";
    try {
      const date = new Date(dateString);
      const now = new Date();
      const diffMs = now.getTime() - date.getTime();
      const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));

      if (diffDays === 0) return "Today";
      if (diffDays === 1) return "Yesterday";
      if (diffDays < 7) return `${diffDays} days ago`;
      if (diffDays < 30) return `${Math.floor(diffDays / 7)} weeks ago`;
      if (diffDays < 365) return `${Math.floor(diffDays / 30)} months ago`;
      return `${Math.floor(diffDays / 365)} years ago`;
    } catch {
      return "";
    }
  }

  function openEditDialog(storeName?: string) {
    editStoreName = storeName ?? defaultStoreName;
    editInitialTab = "general";
    showEditDialog = true;
  }

  function openEditDialogWithTab(tab: "general" | "basal" | "carbratio" | "sens" | "targets") {
    editStoreName = defaultStoreName;
    editInitialTab = tab;
    showEditDialog = true;
  }

  function openDeleteDialog(profile: Profile) {
    profileToDelete = profile;
    showDeleteDialog = true;
  }

  // Command handlers using remote functions
  async function handleCreateProfile(profileData: CreateProfileData) {
    isLoading = true;
    try {
      const result = await createProfile({
        defaultProfile: profileData.defaultProfile,
        dia: profileData.dia,
        carbs_hr: profileData.carbs_hr,
        timezone: profileData.timezone,
        units: profileData.units,
        icon: profileData.icon,
      });

      showCreateDialog = false;
      // Navigate to the new profile
      if (result.createdProfile?._id) {
        selectProfile(result.createdProfile._id);
      }
    } catch (error) {
      console.error("Error creating profile:", error);
    } finally {
      isLoading = false;
    }
  }

  async function handleSaveProfile(profile: Profile) {
    if (!profile._id) return;
    isLoading = true;

    try {
      await updateProfile({
        profileId: profile._id,
        profileData: profile,
      });

      showEditDialog = false;
    } catch (error) {
      console.error("Error updating profile:", error);
    } finally {
      isLoading = false;
    }
  }

  async function handleDeleteProfile() {
    if (!profileToDelete?._id) return;
    isLoading = true;

    try {
      const deletedId = profileToDelete._id;
      await deleteProfile(deletedId);

      showDeleteDialog = false;
      profileToDelete = null;

      // Navigate to another profile if we deleted the selected one
      if (deletedId === selectedProfileId) {
        const currentData = profilesQuery.current;
        const remaining =
          currentData?.profiles.filter((p: Profile) => p._id !== deletedId) ??
          [];
        selectProfile(remaining[0]?._id ?? null);
      }
    } catch (error) {
      console.error("Error deleting profile:", error);
    } finally {
      isLoading = false;
    }
  }
</script>

<svelte:head>
  <title>Profile - Nocturne</title>
  <meta
    name="description"
    content="Manage your diabetes therapy profile settings"
  />
</svelte:head>

{#await profilesQuery}
  <div class="container mx-auto p-6 max-w-5xl">
    <div class="flex items-center justify-center h-64">
      <div class="animate-pulse text-muted-foreground">Loading profiles...</div>
    </div>
  </div>
{:then data}
  <div class="container mx-auto p-6 max-w-5xl space-y-6">
    <!-- Header -->
    <div class="flex items-start justify-between">
      <div class="flex items-center gap-3">
        <div
          class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10"
        >
          <User class="h-6 w-6 text-primary" />
        </div>
        <div>
          <h1 class="text-3xl font-bold tracking-tight">Profile</h1>
          <p class="text-muted-foreground">
            Your therapy settings and insulin parameters
          </p>
        </div>
      </div>
      <div class="flex items-center gap-2">
        <Button onclick={() => (showCreateDialog = true)}>
          <Plus class="h-4 w-4 mr-2" />
          New Profile
        </Button>
        <Badge variant="secondary" class="gap-1">
          <History class="h-3 w-3" />
          {data.totalProfiles} profile{data.totalProfiles !== 1 ? "s" : ""}
        </Badge>
      </div>
    </div>

    {#if !data.currentProfile}
      <!-- Empty State -->
      <Card class="border-dashed">
        <CardContent class="py-12">
          <div class="text-center space-y-4">
            <div
              class="mx-auto flex h-16 w-16 items-center justify-center rounded-full bg-muted"
            >
              <User class="h-8 w-8 text-muted-foreground" />
            </div>
            <div>
              <h3 class="text-lg font-semibold">No Profile Found</h3>
              <p class="text-sm text-muted-foreground max-w-md mx-auto mt-1">
                Profiles are typically uploaded from your diabetes management
                app (like AAPS, Loop, or xDrip+). They contain your basal rates,
                insulin sensitivity factors, and carb ratios.
              </p>
            </div>
            <div class="flex items-center justify-center gap-2">
              <Button onclick={() => (showCreateDialog = true)}>
                <Plus class="h-4 w-4 mr-2" />
                Create Profile
              </Button>
              <Button variant="outline" href="/settings/connectors">
                <Settings class="h-4 w-4 mr-2" />
                Configure Data Sources
              </Button>
            </div>
          </div>
        </CardContent>
      </Card>
    {:else}
      <!-- Profile Selector (if multiple profiles) -->
      {#if data.profiles.length > 1}
        <Card>
          <CardHeader class="pb-3">
            <CardTitle class="text-lg flex items-center gap-2">
              <History class="h-5 w-5" />
              Profile History
            </CardTitle>
            <CardDescription>
              Select a profile to view its settings. The most recently used
              profile is shown first.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <div class="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
              {#each data.profiles as profile}
                {@const ProfileIcon = getProfileIcon(profile)}
                {@const isSelected = selectedProfileId === profile._id}
                {@const isActive = profile._id === data.currentProfile?._id}
                <button
                  class="flex items-center gap-3 p-3 rounded-lg border text-left transition-colors
                       {isSelected
                    ? 'border-primary bg-primary/5'
                    : 'hover:bg-accent/50'}"
                  onclick={() => selectProfile(profile._id ?? null)}
                >
                  <div
                    class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg
                         {isSelected ? 'bg-primary/10' : 'bg-muted'}"
                  >
                    <ProfileIcon
                      class="h-5 w-5 {isSelected
                        ? 'text-primary'
                        : 'text-muted-foreground'}"
                    />
                  </div>
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2">
                      <span class="font-medium truncate">
                        {profile.defaultProfile ?? "Unnamed Profile"}
                      </span>
                      {#if isActive}
                        <Badge
                          variant="default"
                          class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
                        >
                          Active
                        </Badge>
                      {/if}
                    </div>
                    <p class="text-xs text-muted-foreground truncate">
                      {formatRelativeTime(profile.created_at)} • {formatDateDetailed(
                        profile.created_at
                      ).split(",")[0]}
                    </p>
                  </div>
                  <ChevronRight
                    class="h-4 w-4 text-muted-foreground shrink-0 {isSelected
                      ? 'text-primary'
                      : ''}"
                  />
                </button>
              {/each}
            </div>
          </CardContent>
        </Card>
      {/if}

      <!-- Selected Profile Details -->
      {#if selectedProfile}
        <!-- Profile Overview Card -->
        <Card>
          <CardHeader>
            <div class="flex items-start justify-between">
              <div>
                <CardTitle class="flex items-center gap-2">
                  {selectedProfile.defaultProfile ?? "Profile"}
                  {#if selectedProfile._id === data.currentProfile?._id}
                    <Badge
                      variant="default"
                      class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100"
                    >
                      Active
                    </Badge>
                  {/if}
                </CardTitle>
                <CardDescription>
                  Created {formatDateDetailed(selectedProfile.created_at)}
                </CardDescription>
              </div>
              <DropdownMenu.Root>
                <DropdownMenu.Trigger>
                  <Button variant="outline" size="icon">
                    <MoreVertical class="h-4 w-4" />
                  </Button>
                </DropdownMenu.Trigger>
                <DropdownMenu.Content align="end">
                  <DropdownMenu.Item onclick={() => openEditDialog()}>
                    <Edit class="h-4 w-4 mr-2" />
                    Edit Profile
                  </DropdownMenu.Item>
                  <DropdownMenu.Separator />
                  <DropdownMenu.Item
                    class="text-destructive focus:text-destructive"
                    onclick={() => openDeleteDialog(selectedProfile!)}
                  >
                    <Trash2 class="h-4 w-4 mr-2" />
                    Delete Profile
                  </DropdownMenu.Item>
                </DropdownMenu.Content>
              </DropdownMenu.Root>
            </div>
          </CardHeader>
          <CardContent>
            <div class="grid grid-cols-2 sm:grid-cols-4 gap-4">
              <div class="space-y-1">
                <span class="text-xs text-muted-foreground">Units</span>
                <p class="font-medium">
                  {selectedProfile.units ?? "mg/dL"}
                </p>
              </div>
              <div class="space-y-1">
                <span class="text-xs text-muted-foreground">Timezone</span>
                <p class="font-medium">
                  {defaultStore?.timezone ?? "Not set"}
                </p>
              </div>
              <div class="space-y-1">
                <span class="text-xs text-muted-foreground">DIA</span>
                <p class="font-medium">
                  {defaultStore?.dia ?? "–"} hours
                </p>
              </div>
              <div class="space-y-1">
                <span class="text-xs text-muted-foreground">Carbs/hr</span>
                <p class="font-medium">
                  {defaultStore?.carbs_hr ?? "–"} g/hr
                </p>
              </div>
            </div>
          </CardContent>
        </Card>

        <!-- Profile Stores -->
        {#if profileStoreNames.length > 0}
          <Tabs.Root value={defaultStoreName || profileStoreNames[0]}>
            <Tabs.List class="justify-start">
              {#each profileStoreNames as storeName}
                <Tabs.Trigger value={storeName} class="gap-2">
                  <User class="h-4 w-4" />
                  {storeName}
                  {#if storeName === defaultStoreName}
                    <Badge variant="secondary" class="text-xs ml-1">
                      Default
                    </Badge>
                  {/if}
                </Tabs.Trigger>
              {/each}
            </Tabs.List>

            {#each profileStoreNames as storeName}
              {@const store = selectedProfile.store?.[storeName]}
              <Tabs.Content value={storeName} class="mt-4 space-y-4">
                {#if store}
                  <!-- Time-based Settings Grid -->
                  <div class="grid gap-4 md:grid-cols-2">
                    <!-- Basal Rates -->
                    {#if store.basal && store.basal.length > 0}
                      {@render ProfileTimeValueCard({
                        title: "Basal Rates",
                        description: "Background insulin delivery rates",
                        unit: "U/hr",
                        icon: Activity,
                        values: store.basal,
                        colorClass: "text-blue-600",
                        editTab: "basal",
                      })}
                    {/if}

                    <!-- Carb Ratios -->
                    {#if store.carbratio && store.carbratio.length > 0}
                      {@render ProfileTimeValueCard({
                        title: "Carb Ratios (I:C)",
                        description: "Grams of carbs per unit of insulin",
                        unit: "g/U",
                        icon: Droplet,
                        values: store.carbratio,
                        colorClass: "text-green-600",
                        editTab: "carbratio",
                      })}
                    {/if}

                    <!-- Insulin Sensitivity -->
                    {#if store.sens && store.sens.length > 0}
                      {@render ProfileTimeValueCard({
                        title: "Insulin Sensitivity (ISF)",
                        description: "BG drop per unit of insulin",
                        unit: `${bgLabel()}/U`,
                        icon: TrendingUp,
                        values: store.sens.map((v) => ({
                          ...v,
                          value: convertValue(
                            v.value,
                            selectedProfile.units,
                            glucoseUnits.current
                          ),
                        })),
                        colorClass: "text-purple-600",
                        editTab: "sens",
                      })}
                    {/if}

                    <!-- Target Range -->
                    {#if (store.target_low && store.target_low.length > 0) || (store.target_high && store.target_high.length > 0)}
                      <Card>
                        <CardHeader class="pb-3">
                          <div class="flex items-center justify-between">
                            <div class="flex items-center gap-3">
                              <div
                                class="flex h-10 w-10 items-center justify-center rounded-lg bg-amber-500/10"
                              >
                                <Target class="h-5 w-5 text-amber-600" />
                              </div>
                              <div>
                                <CardTitle class="text-base">
                                  Target Range
                                </CardTitle>
                                <CardDescription class="text-xs">
                                  Desired blood glucose range
                                </CardDescription>
                              </div>
                            </div>
                            <Button
                              variant="ghost"
                              size="icon"
                              class="h-8 w-8 text-muted-foreground hover:text-foreground"
                              onclick={() => openEditDialogWithTab("targets")}
                            >
                              <Edit class="h-4 w-4" />
                            </Button>
                          </div>
                        </CardHeader>
                        <CardContent>
                          <Table.Root>
                            <Table.Header>
                              <Table.Row>
                                <Table.Head>Time</Table.Head>
                                <Table.Head class="text-right">Low ({bgLabel()})</Table.Head>
                                <Table.Head class="text-right">High ({bgLabel()})</Table.Head>
                              </Table.Row>
                            </Table.Header>
                            <Table.Body>
                              {@const lowValues = store.target_low ?? []}
                              {@const highValues = store.target_high ?? []}
                              {@const maxLen = Math.max(
                                lowValues.length,
                                highValues.length
                              )}
                              {#each Array(maxLen) as _, i}
                                <Table.Row>
                                  <Table.Cell class="font-mono text-sm">
                                    {lowValues[i]?.time ??
                                      highValues[i]?.time ??
                                      "–"}
                                  </Table.Cell>
                                  <Table.Cell class="text-right font-mono">
                                    {convertValue(
                                      lowValues[i]?.value,
                                      selectedProfile.units,
                                      glucoseUnits.current
                                    ) ?? "–"}
                                  </Table.Cell>
                                  <Table.Cell class="text-right font-mono">
                                    {convertValue(
                                      highValues[i]?.value,
                                      selectedProfile.units,
                                      glucoseUnits.current
                                    ) ?? "–"}
                                  </Table.Cell>
                                </Table.Row>
                              {/each}
                            </Table.Body>
                          </Table.Root>
                        </CardContent>
                      </Card>
                    {/if}
                  </div>

                  <!-- Additional Store Metadata -->
                  <Card class="bg-muted/30">
                    <CardHeader class="pb-3">
                      <CardTitle class="text-sm font-medium">
                        Profile Settings
                      </CardTitle>
                    </CardHeader>
                    <CardContent>
                      <div
                        class="grid grid-cols-2 sm:grid-cols-4 gap-4 text-sm"
                      >
                        <div>
                          <span class="text-muted-foreground">DIA</span>
                          <p class="font-medium">{store.dia ?? "–"} hours</p>
                        </div>
                        <div>
                          <span class="text-muted-foreground">Carbs/hr</span>
                          <p class="font-medium">
                            {store.carbs_hr ?? "–"} g/hr
                          </p>
                        </div>
                        <div>
                          <span class="text-muted-foreground">Timezone</span>
                          <p class="font-medium">{store.timezone ?? "–"}</p>
                        </div>
                        <div>
                          <span class="text-muted-foreground">Units</span>
                          <p class="font-medium">{store.units ?? "–"}</p>
                        </div>
                      </div>
                    </CardContent>
                  </Card>
                {:else}
                  <div class="text-center py-8 text-muted-foreground">
                    <p>No data available for this profile store.</p>
                  </div>
                {/if}
              </Tabs.Content>
            {/each}
          </Tabs.Root>
        {:else}
          <Card class="border-dashed">
            <CardContent class="py-8">
              <div class="text-center text-muted-foreground">
                <p>
                  This profile doesn't contain any therapy settings (basal, carb
                  ratios, etc.)
                </p>
                <Button
                  variant="outline"
                  class="mt-4"
                  onclick={() => openEditDialog()}
                >
                  <Edit class="h-4 w-4 mr-2" />
                  Add Settings
                </Button>
              </div>
            </CardContent>
          </Card>
        {/if}
      {/if}
    {/if}
  </div>
{:catch error}
  <div class="container mx-auto p-6 max-w-5xl">
    <Card class="border-destructive">
      <CardContent class="py-8">
        <div class="text-center space-y-2">
          <p class="text-destructive font-medium">Failed to load profiles</p>
          <p class="text-sm text-muted-foreground">
            {error instanceof Error ? error.message : "An error occurred"}
          </p>
          <Button variant="outline" onclick={() => window.location.reload()}>
            Try again
          </Button>
        </div>
      </CardContent>
    </Card>
  </div>
{/await}

<!-- Dialogs -->
<ProfileCreateDialog
  bind:open={showCreateDialog}
  {isLoading}
  onClose={() => (showCreateDialog = false)}
  onSave={handleCreateProfile}
/>

<ProfileEditDialog
  bind:open={showEditDialog}
  profile={selectedProfile ?? null}
  storeName={editStoreName}
  initialTab={editInitialTab}
  {isLoading}
  onClose={() => {
    showEditDialog = false;
    editStoreName = null;
    editInitialTab = "general";
  }}
  onSave={handleSaveProfile}
/>

<ProfileDeleteDialog
  bind:open={showDeleteDialog}
  profile={profileToDelete}
  {isLoading}
  onClose={() => {
    showDeleteDialog = false;
    profileToDelete = null;
  }}
  onConfirm={handleDeleteProfile}
/>

<!-- Profile Time Value Card Component -->
{#snippet ProfileTimeValueCard({
  title,
  description,
  unit,
  icon: Icon,
  values,
  colorClass,
  editTab,
}: {
  title: string;
  description: string;
  unit: string;
  icon: typeof Activity;
  values: TimeValue[];
  colorClass: string;
  editTab?: "basal" | "carbratio" | "sens" | "targets";
})}
  <Card>
    <CardHeader class="pb-3">
      <div class="flex items-center justify-between">
        <div class="flex items-center gap-3">
          <div
            class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
          >
            <Icon class="h-5 w-5 {colorClass}" />
          </div>
          <div>
            <CardTitle class="text-base">{title}</CardTitle>
            <CardDescription class="text-xs">{description}</CardDescription>
          </div>
        </div>
        {#if editTab}
          <Button
            variant="ghost"
            size="icon"
            class="h-8 w-8 text-muted-foreground hover:text-foreground"
            onclick={() => openEditDialogWithTab(editTab)}
          >
            <Edit class="h-4 w-4" />
          </Button>
        {/if}
      </div>
    </CardHeader>
    <CardContent>
      <Table.Root>
        <Table.Header>
          <Table.Row>
            <Table.Head>Time</Table.Head>
            <Table.Head class="text-right">{unit}</Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each values as timeValue}
            <Table.Row>
              <Table.Cell class="font-mono text-sm">
                {timeValue.time ?? "–"}
              </Table.Cell>
              <Table.Cell class="text-right font-mono">
                {timeValue.value ?? "–"}
              </Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
    </CardContent>
  </Card>
{/snippet}
