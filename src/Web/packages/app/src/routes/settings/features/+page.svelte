<script lang="ts">
  import { getSettingsStore } from "$lib/stores/settings-store.svelte";
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
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
  } from "$lib/components/ui/select";
  import {
    Sparkles,
    Eye,
    Layout,
    BarChart3,
    Syringe,
    Activity,
    Clock,
    Pill,
    Droplets,
    AlertCircle,
  } from "lucide-svelte";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";

  const store = getSettingsStore();

  function enableAllPlugins() {
    if (store.features?.plugins) {
      Object.keys(store.features.plugins).forEach((key) => {
        store.features!.plugins![key].enabled = true;
      });
      store.markChanged();
    }
  }

  function disableAllPlugins() {
    if (store.features?.plugins) {
      Object.keys(store.features.plugins).forEach((key) => {
        store.features!.plugins![key].enabled = false;
      });
      store.markChanged();
    }
  }
</script>

<svelte:head>
  <title>Features - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-3xl space-y-6">
  <!-- Header -->
  <div>
    <h1 class="text-2xl font-bold tracking-tight">Features</h1>
    <p class="text-muted-foreground">
      Customize display options, plugins, and dashboard widgets
    </p>
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
  {:else if store.features}
    <!-- Chart Options -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Eye class="h-5 w-5" />
          Chart Options
        </CardTitle>
        <CardDescription>Configure chart display preferences</CardDescription>
      </CardHeader>
      <CardContent class="space-y-6">
        <div class="grid gap-4 sm:grid-cols-2">
          <div class="space-y-2">
            <Label>Default chart range</Label>
            <Select
              type="single"
              value={String(store.features.display?.focusHours ?? 3)}
              onValueChange={(value) => {
                if (store.features?.display) {
                  store.features.display.focusHours = parseInt(value);
                  store.markChanged();
                }
              }}
            >
              <SelectTrigger>
                <span>{store.features.display?.focusHours ?? 3} hours</span>
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

        <div class="flex items-center justify-between">
          <div class="space-y-0.5">
            <Label>Show raw sensor values</Label>
            <p class="text-sm text-muted-foreground">
              Display unfiltered CGM data alongside calibrated values
            </p>
          </div>
          <Switch
            checked={store.features.display?.showRawBG ?? false}
            onCheckedChange={(checked) => {
              if (store.features?.display) {
                store.features.display.showRawBG = checked;
                store.markChanged();
              }
            }}
          />
        </div>
      </CardContent>
    </Card>

    <!-- Dashboard Widgets -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Layout class="h-5 w-5" />
          Dashboard Widgets
        </CardTitle>
        <CardDescription>
          Choose which widgets appear on your dashboard
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="grid gap-4 sm:grid-cols-2">
          <div class="flex items-center justify-between p-3 rounded-lg border">
            <div class="flex items-center gap-3">
              <Activity class="h-5 w-5 text-muted-foreground" />
              <Label>Glucose Chart</Label>
            </div>
            <Switch
              checked={store.features.dashboardWidgets?.glucoseChart ?? true}
              onCheckedChange={(checked) => {
                if (store.features?.dashboardWidgets) {
                  store.features.dashboardWidgets.glucoseChart = checked;
                  store.markChanged();
                }
              }}
            />
          </div>

          <div class="flex items-center justify-between p-3 rounded-lg border">
            <div class="flex items-center gap-3">
              <BarChart3 class="h-5 w-5 text-muted-foreground" />
              <Label>Statistics</Label>
            </div>
            <Switch
              checked={store.features.dashboardWidgets?.statistics ?? true}
              onCheckedChange={(checked) => {
                if (store.features?.dashboardWidgets) {
                  store.features.dashboardWidgets.statistics = checked;
                  store.markChanged();
                }
              }}
            />
          </div>

          <div class="flex items-center justify-between p-3 rounded-lg border">
            <div class="flex items-center gap-3">
              <Syringe class="h-5 w-5 text-muted-foreground" />
              <Label>Treatments</Label>
            </div>
            <Switch
              checked={store.features.dashboardWidgets?.treatments ?? true}
              onCheckedChange={(checked) => {
                if (store.features?.dashboardWidgets) {
                  store.features.dashboardWidgets.treatments = checked;
                  store.markChanged();
                }
              }}
            />
          </div>

          <div class="flex items-center justify-between p-3 rounded-lg border">
            <div class="flex items-center gap-3">
              <Activity class="h-5 w-5 text-muted-foreground" />
              <Label>Predictions</Label>
            </div>
            <Switch
              checked={store.features.dashboardWidgets?.predictions ?? true}
              onCheckedChange={(checked) => {
                if (store.features?.dashboardWidgets) {
                  store.features.dashboardWidgets.predictions = checked;
                  store.markChanged();
                }
              }}
            />
          </div>

          <div class="flex items-center justify-between p-3 rounded-lg border">
            <div class="flex items-center gap-3">
              <BarChart3 class="h-5 w-5 text-muted-foreground" />
              <Label>AGP Summary</Label>
            </div>
            <Switch
              checked={store.features.dashboardWidgets?.agp ?? false}
              onCheckedChange={(checked) => {
                if (store.features?.dashboardWidgets) {
                  store.features.dashboardWidgets.agp = checked;
                  store.markChanged();
                }
              }}
            />
          </div>

          <div class="flex items-center justify-between p-3 rounded-lg border">
            <div class="flex items-center gap-3">
              <Clock class="h-5 w-5 text-muted-foreground" />
              <Label>Daily Stats</Label>
            </div>
            <Switch
              checked={store.features.dashboardWidgets?.dailyStats ?? true}
              onCheckedChange={(checked) => {
                if (store.features?.dashboardWidgets) {
                  store.features.dashboardWidgets.dailyStats = checked;
                  store.markChanged();
                }
              }}
            />
          </div>
        </div>
      </CardContent>
    </Card>

    <!-- Plugins -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Sparkles class="h-5 w-5" />
          Plugins
        </CardTitle>
        <CardDescription>
          Enable or disable individual data plugins
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div class="space-y-1">
          <!-- Age-related plugins (cage, sage, iage, bage, upbat) are now managed in Appearance > Tracker Pills -->
          {#each Object.entries(store.features.plugins ?? {}).filter(([key]) => !["cage", "sage", "iage", "bage", "upbat"].includes(key)) as [key, plugin]}
            <div
              class="flex items-center justify-between py-3 border-b last:border-0"
            >
              <div class="flex items-center gap-3">
                {#if key === "delta" || key === "direction"}
                  <Activity class="h-4 w-4 text-muted-foreground" />
                {:else if key === "timeago"}
                  <Clock class="h-4 w-4 text-muted-foreground" />
                {:else if key === "iob" || key === "basal"}
                  <Syringe class="h-4 w-4 text-muted-foreground" />
                {:else if key === "cob"}
                  <Droplets class="h-4 w-4 text-muted-foreground" />
                {:else}
                  <Pill class="h-4 w-4 text-muted-foreground" />
                {/if}
                <div>
                  <Label class="capitalize">{key}</Label>
                  <p class="text-sm text-muted-foreground">
                    {plugin.description ?? ""}
                  </p>
                </div>
              </div>
              <Switch
                checked={plugin.enabled ?? false}
                onCheckedChange={(checked) => {
                  if (store.features?.plugins) {
                    store.features.plugins[key].enabled = checked;
                    store.markChanged();
                  }
                }}
              />
            </div>
          {/each}
        </div>
      </CardContent>
    </Card>

    <!-- Quick Enable/Disable All -->
    <div class="flex justify-end gap-2">
      <Button variant="outline" size="sm" onclick={disableAllPlugins}>
        Disable All Plugins
      </Button>
      <Button variant="outline" size="sm" onclick={enableAllPlugins}>
        Enable All Plugins
      </Button>
    </div>
  {/if}
</div>
