<script lang="ts">
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
  import {
    User,
    Activity,
    Droplet,
    Target,
    TrendingUp,
    Clock,
    Settings,
    ChevronRight,
    Lock,
  } from "lucide-svelte";
  import * as Alert from "$lib/components/ui/alert";
  import { bgLabel } from "$lib/utils/formatting";
  import { getProfileSummary } from "$api/generated/profiles.generated.remote";
  import { coachmark } from "@nocturne/coach";
  import ScheduleView from "$lib/components/schedule/ScheduleView.svelte";

  type Summary = Awaited<ReturnType<typeof getProfileSummary>>;

  // Query for profile summary data
  const summaryQuery = $derived(getProfileSummary(undefined));

  // Selected profile name from URL or default
  const urlProfileName = $derived(page.url.searchParams.get("name"));

  // Extract unique profile names from therapy settings (the canonical source)
  function getProfileNames(data: Summary): string[] {
    const names = new Set<string>();
    for (const ts of (data?.therapySettings ?? []) as any[]) {
      names.add(String(ts.profileName ?? "Default"));
    }
    return [...names];
  }

  // Determine the default (active) profile name
  function getDefaultProfileName(data: Summary): string | null {
    const settings = (data?.therapySettings ?? []) as any[];
    const defaultSettings = settings.find((ts: any) => ts.isDefault) ?? settings[0] ?? null;
    return defaultSettings?.profileName ?? null;
  }

  // Helper to extract data from the summary for a given profile name
  function getTherapyForProfile(data: Summary, profileName: string) {
    return ((data?.therapySettings ?? []) as any[]).find((ts: any) => ts.profileName === profileName) ?? null;
  }

  function getBasalForProfile(data: Summary, profileName: string) {
    return ((data?.basalSchedules ?? []) as any[]).find((b: any) => b.profileName === profileName) ?? null;
  }

  function getCarbRatioForProfile(data: Summary, profileName: string) {
    return ((data?.carbRatioSchedules ?? []) as any[]).find((c: any) => c.profileName === profileName) ?? null;
  }

  function getSensitivityForProfile(data: Summary, profileName: string) {
    return ((data?.sensitivitySchedules ?? []) as any[]).find((s: any) => s.profileName === profileName) ?? null;
  }

  function getTargetRangeForProfile(data: Summary, profileName: string) {
    return ((data?.targetRangeSchedules ?? []) as any[]).find((t: any) => t.profileName === profileName) ?? null;
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
</script>

<svelte:head>
  <title>Profile - Nocturne</title>
  <meta
    name="description"
    content="Manage your diabetes therapy profile settings"
  />
</svelte:head>

{#await summaryQuery}
  <div class="container mx-auto max-w-4xl p-6 space-y-6">
    <div class="flex items-center justify-center h-64">
      <div class="animate-pulse text-muted-foreground">Loading profiles...</div>
    </div>
  </div>
{:then data}
  {@const profileNames = getProfileNames(data)}
  {@const defaultProfileName = getDefaultProfileName(data)}
  {@const selectedProfileName = urlProfileName ?? defaultProfileName ?? profileNames[0] ?? null}
  {@const therapy = selectedProfileName ? getTherapyForProfile(data, selectedProfileName) : null}
  {@const basal = selectedProfileName ? getBasalForProfile(data, selectedProfileName) : null}
  {@const carbRatio = selectedProfileName ? getCarbRatioForProfile(data, selectedProfileName) : null}
  {@const sensitivity = selectedProfileName ? getSensitivityForProfile(data, selectedProfileName) : null}
  {@const targetRange = selectedProfileName ? getTargetRangeForProfile(data, selectedProfileName) : null}
  {@const profileConfigured = (data?.basalSchedules ?? []).length > 0}
  <div class="container mx-auto max-w-4xl p-6 space-y-6" {@attach coachmark({ key: "onboarding.therapy-profile", title: "Automatically synced", description: "Your basal rates, carb ratios, and sensitivity factors are imported from your uploader (AAPS, Loop, xDrip+). No manual entry needed.", completedWhen: () => profileConfigured })}>
    <!-- Header -->
    <div class="flex items-start justify-between">
      <div class="flex items-center gap-3">
        <div
          class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10"
        >
          <User class="h-6 w-6 text-primary" />
        </div>
        <div>
          <h1 class="text-2xl font-bold tracking-tight">Profile</h1>
          <p class="text-muted-foreground">
            Your therapy settings and insulin parameters
          </p>
        </div>
      </div>
      <Badge variant="secondary" class="gap-1">
        <Clock class="h-3 w-3" />
        {profileNames.length} profile{profileNames.length !== 1 ? "s" : ""}
      </Badge>
    </div>

    {#if profileNames.length === 0}
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
              <Button variant="outline" href="/settings/connectors">
                <Settings class="h-4 w-4 mr-2" />
                Configure Data Sources
              </Button>
            </div>
          </div>
        </CardContent>
      </Card>
    {:else}
      <!-- Profile Name Tabs (when multiple profiles exist) -->
      {#if profileNames.length > 1}
        <Card>
          <CardHeader class="pb-3">
            <CardTitle class="text-lg flex items-center gap-2">
              <User class="h-5 w-5" />
              Profiles
            </CardTitle>
            <CardDescription>
              Select a profile to view its settings.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <div class="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
              {#each profileNames as profileName}
                {@const profileTherapy = getTherapyForProfile(data, profileName)}
                {@const isSelected = selectedProfileName === profileName}
                {@const isDefault = profileTherapy?.isDefault === true}
                {@const isExternal = profileTherapy?.isExternallyManaged === true}
                <a
                  href="?name={encodeURIComponent(profileName)}"
                  data-sveltekit-noscroll
                  data-sveltekit-replacestate
                  class="flex items-center gap-3 p-3 rounded-lg border text-left transition-colors
                       {isSelected
                    ? 'border-primary bg-primary/5'
                    : 'hover:bg-accent/50'}"
                >
                  <div
                    class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg
                         {isSelected ? 'bg-primary/10' : 'bg-muted'}"
                  >
                    {#if isExternal}
                      <Lock
                        class="h-5 w-5 {isSelected
                          ? 'text-primary'
                          : 'text-muted-foreground'}"
                      />
                    {:else}
                      <User
                        class="h-5 w-5 {isSelected
                          ? 'text-primary'
                          : 'text-muted-foreground'}"
                      />
                    {/if}
                  </div>
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2">
                      <span class="font-medium truncate">
                        {profileName}
                      </span>
                      {#if isDefault}
                        <Badge
                          variant="default"
                          class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
                        >
                          Active
                        </Badge>
                      {/if}
                      {#if isExternal}
                        <Badge variant="outline" class="text-xs gap-1">
                          <Lock class="h-3 w-3" />
                          {profileTherapy?.enteredBy ?? "External"}
                        </Badge>
                      {/if}
                    </div>
                    {#if profileTherapy?.createdAt}
                      <p class="text-xs text-muted-foreground truncate">
                        {formatRelativeTime(profileTherapy.createdAt)}
                      </p>
                    {/if}
                  </div>
                  <ChevronRight
                    class="h-4 w-4 text-muted-foreground shrink-0 {isSelected
                      ? 'text-primary'
                      : ''}"
                  />
                </a>
              {/each}
            </div>
          </CardContent>
        </Card>
      {/if}

      <!-- Selected Profile Details -->
      {#if selectedProfileName && therapy}
        {#if therapy.isExternallyManaged}
          <Alert.Root class="border-muted-foreground/25 bg-muted/50">
            <Lock class="h-4 w-4" />
            <Alert.Title>Managed by {therapy.enteredBy ?? "an external source"}</Alert.Title>
            <Alert.Description>
              This profile is read-only because it is synced from an external device or service. Changes to therapy settings must be made on the source device.
            </Alert.Description>
          </Alert.Root>
        {/if}

        <!-- Profile Overview Card -->
        <Card>
          <CardHeader>
            <div class="flex items-start justify-between">
              <div>
                <CardTitle class="flex items-center gap-2">
                  {selectedProfileName}
                  {#if therapy.isDefault}
                    <Badge
                      variant="default"
                      class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100"
                    >
                      Active
                    </Badge>
                  {/if}
                  {#if therapy.isExternallyManaged}
                    <Badge variant="outline" class="gap-1">
                      <Lock class="h-3 w-3" />
                      Read-only
                    </Badge>
                  {/if}
                </CardTitle>
                {#if therapy.createdAt}
                  <CardDescription>
                    Created {formatRelativeTime(therapy.createdAt)}
                    {#if therapy.enteredBy}
                      &middot; Entered by {therapy.enteredBy}
                    {/if}
                  </CardDescription>
                {/if}
              </div>
            </div>
          </CardHeader>
          <CardContent>
            <div class="grid grid-cols-2 sm:grid-cols-4 gap-4">
              <div class="space-y-1">
                <span class="text-xs text-muted-foreground">Units</span>
                <p class="font-medium">
                  {therapy.units ?? "mg/dL"}
                </p>
              </div>
              <div class="space-y-1">
                <span class="text-xs text-muted-foreground">Timezone</span>
                <p class="font-medium">
                  {therapy.timezone ?? "Not set"}
                </p>
              </div>
              <div class="space-y-1">
                <span class="text-xs text-muted-foreground">DIA</span>
                <p class="font-medium">
                  {therapy.dia ?? "-"} hours
                </p>
              </div>
              <div class="space-y-1">
                <span class="text-xs text-muted-foreground">Carbs/hr</span>
                <p class="font-medium">
                  {therapy.carbsHr ?? "-"} g/hr
                </p>
              </div>
            </div>
          </CardContent>
        </Card>

        <!-- Schedule Cards Grid -->
        <div class="grid gap-4 md:grid-cols-2">
          {#if basal?.entries && basal.entries.length > 0}
            <ScheduleView
              title="Basal Rates"
              description="Background insulin delivery rates"
              unit="U/hr"
              icon={Activity}
              iconClass="text-blue-600"
              entries={basal.entries}
            />
          {/if}

          {#if carbRatio?.entries && carbRatio.entries.length > 0}
            <ScheduleView
              title="Carb Ratios (I:C)"
              description="Grams of carbs per unit of insulin"
              unit="g/U"
              icon={Droplet}
              iconClass="text-green-600"
              entries={carbRatio.entries}
            />
          {/if}

          {#if sensitivity?.entries && sensitivity.entries.length > 0}
            <ScheduleView
              title="Insulin Sensitivity (ISF)"
              description="BG drop per unit of insulin"
              unit="{bgLabel()}/U"
              icon={TrendingUp}
              iconClass="text-purple-600"
              entries={sensitivity.entries}
              sourceUnits={therapy.units}
            />
          {/if}

          {#if targetRange?.entries && targetRange.entries.length > 0}
            <ScheduleView
              title="Target Range"
              description="Desired blood glucose range"
              unit={bgLabel()}
              icon={Target}
              iconClass="text-amber-600"
              entries={targetRange.entries}
              sourceUnits={therapy.units}
            />
          {/if}
        </div>

        <!-- Additional Therapy Metadata -->
        <Card class="bg-muted/30">
          <CardHeader class="pb-3">
            <CardTitle class="text-sm font-medium">
              Profile Settings
            </CardTitle>
          </CardHeader>
          <CardContent>
            <div class="grid grid-cols-2 sm:grid-cols-4 gap-4 text-sm">
              <div>
                <span class="text-muted-foreground">DIA</span>
                <p class="font-medium">{therapy.dia ?? "-"} hours</p>
              </div>
              <div>
                <span class="text-muted-foreground">Carbs/hr</span>
                <p class="font-medium">{therapy.carbsHr ?? "-"} g/hr</p>
              </div>
              <div>
                <span class="text-muted-foreground">Delay</span>
                <p class="font-medium">{therapy.delay ?? "-"} min</p>
              </div>
              <div>
                <span class="text-muted-foreground">Units</span>
                <p class="font-medium">{therapy.units ?? "-"}</p>
              </div>
            </div>
          </CardContent>
        </Card>
      {:else if selectedProfileName}
        <!-- Profile selected but no therapy settings found -->
        <Card class="border-dashed">
          <CardContent class="py-8">
            <div class="text-center text-muted-foreground">
              <p>
                No therapy settings found for profile "{selectedProfileName}".
              </p>
            </div>
          </CardContent>
        </Card>
      {/if}
    {/if}
  </div>
{:catch error}
  <div class="container mx-auto max-w-4xl p-6 space-y-6">
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

