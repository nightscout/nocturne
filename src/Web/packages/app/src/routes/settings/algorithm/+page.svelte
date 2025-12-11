<script lang="ts">
  import { getSettingsStore } from "$lib/stores/settings-store.svelte";
  import {
    predictionMinutes,
    predictionEnabled,
  } from "$lib/stores/appearance-store.svelte";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Switch } from "$lib/components/ui/switch";
  import { Separator } from "$lib/components/ui/separator";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
  } from "$lib/components/ui/select";
  import {
    TrendingUp,
    Gauge,
    AlertTriangle,
    Zap,
    Clock,
    Shield,
  } from "lucide-svelte";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";

  const store = getSettingsStore();

  // Sync server settings to appearance store when loaded
  $effect(() => {
    if (store.algorithm?.prediction?.minutes) {
      // Only update if significantly different to avoid loops, though PersistedState handles it well
      if (store.algorithm.prediction.minutes !== predictionMinutes.current) {
        predictionMinutes.current = store.algorithm.prediction.minutes;
      }
    }
    // Sync enabled state
    if (store.algorithm?.prediction?.enabled !== undefined) {
      if (store.algorithm.prediction.enabled !== predictionEnabled.current) {
        predictionEnabled.current = store.algorithm.prediction.enabled;
      }
    }
  });
</script>

<svelte:head>
  <title>Algorithm - Settings - Nocturne</title>
</svelte:head>

{#if store.isLoading}
  <SettingsPageSkeleton cardCount={4} />
{:else if store.hasError}
  <div class="container mx-auto p-6 max-w-3xl">
    <Card class="border-destructive">
      <CardContent class="flex items-center gap-3 pt-6">
        <AlertTriangle class="h-5 w-5 text-destructive" />
        <div>
          <p class="font-medium">Failed to load settings</p>
          <p class="text-sm text-muted-foreground">{store.error}</p>
        </div>
      </CardContent>
    </Card>
  </div>
{:else if store.algorithm}
  <div class="container mx-auto p-6 max-w-3xl space-y-6">
    <!-- Header -->
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Algorithm Settings</h1>
      <p class="text-muted-foreground">
        Configure prediction algorithms and automation parameters
      </p>
    </div>

    <!-- Prediction Settings -->
    <Card>
      <CardHeader>
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-3">
            <div
              class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
            >
              <TrendingUp class="h-5 w-5 text-primary" />
            </div>
            <div>
              <CardTitle>Glucose Predictions</CardTitle>
              <CardDescription>
                Configure how future glucose values are predicted
              </CardDescription>
            </div>
          </div>
          <Switch
            checked={store.algorithm.prediction?.enabled ?? false}
            onCheckedChange={(checked) => {
              if (store.algorithm?.prediction) {
                store.algorithm.prediction.enabled = checked;
                store.markChanged();
                // Update appearance store
                predictionEnabled.current = checked;
              }
            }}
          />
        </div>
      </CardHeader>
      {#if store.algorithm.prediction?.enabled}
        <CardContent class="space-y-6">
          <div class="space-y-2">
            <Label>Prediction horizon</Label>
            <div class="space-y-4">
              <div class="flex items-center gap-4">
                <input
                  type="range"
                  value={store.algorithm.prediction.minutes ?? 30}
                  min={15}
                  max={240}
                  step={15}
                  class="flex-1 h-2 bg-muted rounded-lg appearance-none cursor-pointer"
                  oninput={(e) => {
                    if (store.algorithm?.prediction) {
                      const val = parseInt(e.currentTarget.value);
                      store.algorithm.prediction.minutes = val;
                      store.markChanged();
                      // Update appearance store for instant reactivity
                      predictionMinutes.current = val;
                    }
                  }}
                />
              </div>
              <div class="flex justify-between text-sm text-muted-foreground">
                <span>15 min</span>
                <span class="font-medium text-foreground">
                  {store.algorithm.prediction.minutes ?? 30} minutes
                </span>
                <span>4 hours</span>
              </div>
            </div>
          </div>

          <Separator />

          <div class="space-y-2">
            <Label>Prediction model</Label>
            <Select
              type="single"
              value={store.algorithm.prediction.model ?? "ar2"}
              onValueChange={(value) => {
                if (store.algorithm?.prediction) {
                  store.algorithm.prediction.model = value;
                  store.markChanged();
                }
              }}
            >
              <SelectTrigger>
                <span>
                  {#if store.algorithm.prediction.model === "ar2"}
                    AR2 (Second-order auto-regressive)
                  {:else if store.algorithm.prediction.model === "linear"}
                    Linear extrapolation
                  {:else if store.algorithm.prediction.model === "iob"}
                    IOB-based prediction
                  {:else}
                    Select model
                  {/if}
                </span>
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="ar2">
                  AR2 (Second-order auto-regressive)
                </SelectItem>
                <SelectItem value="linear">Linear extrapolation</SelectItem>
                <SelectItem value="iob">IOB-based prediction</SelectItem>
                <SelectItem value="cob">COB-based prediction</SelectItem>
                <SelectItem value="uam">UAM (Unannounced meals)</SelectItem>
              </SelectContent>
            </Select>
            <p class="text-sm text-muted-foreground">
              The algorithm used to forecast future glucose values
            </p>
          </div>
        </CardContent>
      {/if}
    </Card>

    <!-- Autosens Settings -->
    <Card>
      <CardHeader>
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-3">
            <div
              class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
            >
              <Gauge class="h-5 w-5 text-primary" />
            </div>
            <div>
              <CardTitle>Autosens</CardTitle>
              <CardDescription>
                Automatic sensitivity adjustments based on recent data
              </CardDescription>
            </div>
          </div>
          <Switch
            checked={store.algorithm.autosens?.enabled ?? false}
            onCheckedChange={(checked) => {
              if (store.algorithm?.autosens) {
                store.algorithm.autosens.enabled = checked;
                store.markChanged();
              }
            }}
          />
        </div>
      </CardHeader>
      {#if store.algorithm.autosens?.enabled}
        <CardContent class="space-y-6">
          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-2">
              <Label>Minimum adjustment</Label>
              <div class="flex items-center gap-2">
                <Input
                  type="number"
                  value={store.algorithm.autosens.min ?? 0.7}
                  class="w-24"
                  min="0.1"
                  max="1.0"
                  step="0.05"
                  onchange={(e) => {
                    if (store.algorithm?.autosens) {
                      store.algorithm.autosens.min = parseFloat(
                        e.currentTarget.value
                      );
                      store.markChanged();
                    }
                  }}
                />
                <span class="text-muted-foreground">
                  ({((store.algorithm.autosens.min ?? 0.7) * 100).toFixed(0)}%)
                </span>
              </div>
            </div>
            <div class="space-y-2">
              <Label>Maximum adjustment</Label>
              <div class="flex items-center gap-2">
                <Input
                  type="number"
                  value={store.algorithm.autosens.max ?? 1.2}
                  class="w-24"
                  min="1.0"
                  max="2.0"
                  step="0.05"
                  onchange={(e) => {
                    if (store.algorithm?.autosens) {
                      store.algorithm.autosens.max = parseFloat(
                        e.currentTarget.value
                      );
                      store.markChanged();
                    }
                  }}
                />
                <span class="text-muted-foreground">
                  ({((store.algorithm.autosens.max ?? 1.2) * 100).toFixed(0)}%)
                </span>
              </div>
            </div>
          </div>
          <p class="text-sm text-muted-foreground">
            Autosens will adjust your sensitivity factor within these bounds
            based on recent glucose patterns.
          </p>
        </CardContent>
      {/if}
    </Card>

    <!-- Carb Absorption Settings -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Clock class="h-5 w-5" />
          Carb Absorption
        </CardTitle>
        <CardDescription>
          How the algorithm estimates carbohydrate absorption
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="grid gap-4 sm:grid-cols-2">
          <div class="space-y-2">
            <Label>Default absorption time</Label>
            <div class="flex items-center gap-2">
              <Input
                type="number"
                value={store.algorithm.carbAbsorption?.defaultMinutes ?? 45}
                class="w-24"
                min="10"
                max="120"
                onchange={(e) => {
                  if (store.algorithm?.carbAbsorption) {
                    store.algorithm.carbAbsorption.defaultMinutes = parseInt(
                      e.currentTarget.value
                    );
                    store.markChanged();
                  }
                }}
              />
              <span class="text-muted-foreground">minutes</span>
            </div>
          </div>
          <div class="space-y-2">
            <Label>Minimum absorption rate</Label>
            <div class="flex items-center gap-2">
              <Input
                type="number"
                value={store.algorithm.carbAbsorption?.minRateGramsPerHour ?? 8}
                class="w-24"
                min="1"
                max="30"
                onchange={(e) => {
                  if (store.algorithm?.carbAbsorption) {
                    store.algorithm.carbAbsorption.minRateGramsPerHour =
                      parseInt(e.currentTarget.value);
                    store.markChanged();
                  }
                }}
              />
              <span class="text-muted-foreground">g/hour</span>
            </div>
          </div>
        </div>
      </CardContent>
    </Card>

    <!-- Loop Settings -->
    <Card>
      <CardHeader>
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-3">
            <div
              class="flex h-10 w-10 items-center justify-center rounded-lg bg-amber-500/10"
            >
              <Zap class="h-5 w-5 text-amber-500" />
            </div>
            <div>
              <CardTitle>Closed Loop</CardTitle>
              <CardDescription>
                Automatic insulin delivery adjustments
              </CardDescription>
            </div>
          </div>
          <Switch
            checked={store.algorithm.loop?.enabled ?? false}
            onCheckedChange={(checked) => {
              if (store.algorithm?.loop) {
                store.algorithm.loop.enabled = checked;
                store.markChanged();
              }
            }}
          />
        </div>
      </CardHeader>
      {#if store.algorithm.loop?.enabled}
        <CardContent class="space-y-6">
          <Card
            class="border-amber-200 bg-amber-50/50 dark:border-amber-900 dark:bg-amber-950/20"
          >
            <CardContent class="flex items-start gap-3 pt-6">
              <AlertTriangle
                class="h-5 w-5 text-amber-600 dark:text-amber-400 shrink-0 mt-0.5"
              />
              <div>
                <p class="font-medium text-amber-900 dark:text-amber-100">
                  Advanced Feature
                </p>
                <p class="text-sm text-amber-800 dark:text-amber-200">
                  Closed loop operation requires careful configuration and
                  monitoring. Ensure your therapy settings are accurate.
                </p>
              </div>
            </CardContent>
          </Card>

          <div class="space-y-2">
            <Label>Loop mode</Label>
            <Select
              type="single"
              value={store.algorithm.loop.mode ?? "open"}
              onValueChange={(value) => {
                if (store.algorithm?.loop) {
                  store.algorithm.loop.mode = value;
                  store.markChanged();
                }
              }}
            >
              <SelectTrigger>
                <span>
                  {#if store.algorithm.loop.mode === "open"}
                    Open Loop (suggestions only)
                  {:else if store.algorithm.loop.mode === "closed"}
                    Closed Loop (automatic adjustments)
                  {:else}
                    Select mode
                  {/if}
                </span>
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="open">
                  Open Loop (suggestions only)
                </SelectItem>
                <SelectItem value="closed">
                  Closed Loop (automatic adjustments)
                </SelectItem>
              </SelectContent>
            </Select>
          </div>

          <Separator />

          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-2">
              <Label>Max basal rate</Label>
              <div class="flex items-center gap-2">
                <Input
                  type="number"
                  value={store.algorithm.loop.maxBasalRate ?? 3.0}
                  class="w-24"
                  min="0.1"
                  max="10"
                  step="0.1"
                  onchange={(e) => {
                    if (store.algorithm?.loop) {
                      store.algorithm.loop.maxBasalRate = parseFloat(
                        e.currentTarget.value
                      );
                      store.markChanged();
                    }
                  }}
                />
                <span class="text-muted-foreground">U/hr</span>
              </div>
            </div>
            <div class="space-y-2">
              <Label>Max bolus</Label>
              <div class="flex items-center gap-2">
                <Input
                  type="number"
                  value={store.algorithm.loop.maxBolus ?? 10.0}
                  class="w-24"
                  min="1"
                  max="30"
                  step="0.5"
                  onchange={(e) => {
                    if (store.algorithm?.loop) {
                      store.algorithm.loop.maxBolus = parseFloat(
                        e.currentTarget.value
                      );
                      store.markChanged();
                    }
                  }}
                />
                <span class="text-muted-foreground">U</span>
              </div>
            </div>
          </div>

          <Separator />

          <div class="space-y-4">
            <div class="flex items-center justify-between">
              <div class="space-y-0.5">
                <Label>Super Micro Bolus (SMB)</Label>
                <p class="text-sm text-muted-foreground">
                  Allow small automatic correction boluses
                </p>
              </div>
              <Switch
                checked={store.algorithm.loop.smbEnabled ?? false}
                onCheckedChange={(checked) => {
                  if (store.algorithm?.loop) {
                    store.algorithm.loop.smbEnabled = checked;
                    store.markChanged();
                  }
                }}
              />
            </div>

            <div class="flex items-center justify-between">
              <div class="space-y-0.5">
                <Label>Unannounced Meals (UAM)</Label>
                <p class="text-sm text-muted-foreground">
                  Detect and respond to meals without carb entry
                </p>
              </div>
              <Switch
                checked={store.algorithm.loop.uamEnabled ?? false}
                onCheckedChange={(checked) => {
                  if (store.algorithm?.loop) {
                    store.algorithm.loop.uamEnabled = checked;
                    store.markChanged();
                  }
                }}
              />
            </div>
          </div>
        </CardContent>
      {/if}
    </Card>

    <!-- Safety Limits -->
    <Card>
      <CardHeader>
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-3">
            <div
              class="flex h-10 w-10 items-center justify-center rounded-lg bg-green-500/10"
            >
              <Shield class="h-5 w-5 text-green-500" />
            </div>
            <div>
              <CardTitle>Safety Limits</CardTitle>
              <CardDescription>
                Hard limits to prevent excessive insulin delivery
              </CardDescription>
            </div>
          </div>
          <Badge variant="secondary">Always Active</Badge>
        </div>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="grid gap-4 sm:grid-cols-2">
          <div class="space-y-2">
            <Label>Maximum IOB</Label>
            <div class="flex items-center gap-2">
              <Input
                type="number"
                value={store.algorithm.safetyLimits?.maxIOB ?? 10.0}
                class="w-24"
                min="0"
                max="50"
                step="0.5"
                onchange={(e) => {
                  if (store.algorithm?.safetyLimits) {
                    store.algorithm.safetyLimits.maxIOB = parseFloat(
                      e.currentTarget.value
                    );
                    store.markChanged();
                  }
                }}
              />
              <span class="text-muted-foreground">U</span>
            </div>
            <p class="text-xs text-muted-foreground">
              Maximum insulin on board from boluses
            </p>
          </div>
          <div class="space-y-2">
            <Label>Max daily safety multiplier</Label>
            <div class="flex items-center gap-2">
              <Input
                type="number"
                value={store.algorithm.safetyLimits?.maxDailyBasalMultiplier ??
                  3.0}
                class="w-24"
                min="1"
                max="10"
                step="0.5"
                onchange={(e) => {
                  if (store.algorithm?.safetyLimits) {
                    store.algorithm.safetyLimits.maxDailyBasalMultiplier =
                      parseFloat(e.currentTarget.value);
                    store.markChanged();
                  }
                }}
              />
              <span class="text-muted-foreground">x</span>
            </div>
            <p class="text-xs text-muted-foreground">
              Maximum basal as multiplier of highest scheduled rate
            </p>
          </div>
        </div>
      </CardContent>
    </Card>
  </div>
{/if}
