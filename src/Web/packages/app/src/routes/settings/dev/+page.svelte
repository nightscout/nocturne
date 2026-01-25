<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import {
    createFakeEntry,
    getAlertsDebugSnapshot,
    triggerAlarmForProfile,
  } from "$lib/data/alerts-debug.remote";

  let refreshToken = $state(0);
  const debugQuery = $derived(getAlertsDebugSnapshot({ refreshToken }));

  const alarmConfiguration = $derived(
    debugQuery.current?.alarmConfiguration ?? null
  );
  const latestEntry = $derived(debugQuery.current?.latestEntry ?? null);
  const fetchedAt = $derived(debugQuery.current?.fetchedAt ?? "unknown");

  let selectedProfileId = $state<string | null>(null);
  let fakeSgv = $state<number>(140);
  let fakeDevice = $state("alerts-debug");
  let actionStatus = $state<string | null>(null);
  let actionBusy = $state(false);

  function refresh() {
    refreshToken += 1;
  }

  $effect(() => {
    if (!selectedProfileId && alarmConfiguration?.profiles?.length) {
      selectedProfileId = alarmConfiguration.profiles[0]?.id ?? null;
    }
  });

  async function handleCreateFakeEntry() {
    actionBusy = true;
    actionStatus = null;

    try {
      const result = await createFakeEntry({
        sgv: fakeSgv,
        device: fakeDevice || "alerts-debug",
      });
      if (result?.ok) {
        actionStatus = "Fake entry created";
        refresh();
      } else {
        actionStatus = "Failed to create fake entry";
      }
    } catch (err) {
      console.error("Failed to create fake entry:", err);
      actionStatus = "Failed to create fake entry";
    } finally {
      actionBusy = false;
    }
  }

  async function handleTriggerProfile() {
    if (!selectedProfileId) {
      actionStatus = "Select a profile first";
      return;
    }

    actionBusy = true;
    actionStatus = null;

    try {
      const result = await triggerAlarmForProfile({
        profileId: selectedProfileId,
      });

      if (result?.ok) {
        actionStatus = `Triggered ${result.alarmType} with SGV ${result.usedSgv}`;
        refresh();
      } else if (result?.reason === "unsupported_type") {
        actionStatus = "Selected alarm type is not supported by the backend rules";
      } else {
        actionStatus = "Failed to trigger alarm";
      }
    } catch (err) {
      console.error("Failed to trigger alarm:", err);
      actionStatus = "Failed to trigger alarm";
    } finally {
      actionBusy = false;
    }
  }
</script>

<div class="mx-auto w-full max-w-5xl space-y-6 px-4 py-6 md:px-8">
  <div class="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Alerts Debug</h1>
      <p class="text-muted-foreground">
        Quick snapshot of alert configuration and latest glucose entry
      </p>
    </div>
    <Button on:click={refresh} variant="outline">Refresh</Button>
  </div>

  <Card>
    <CardHeader>
      <CardTitle>Quick Actions</CardTitle>
      <CardDescription>Manually trigger alerts and create test entries</CardDescription>
    </CardHeader>
    <CardContent class="space-y-6">
      <div class="grid gap-4 md:grid-cols-2">
        <div class="space-y-3 rounded-lg border border-border bg-muted/30 p-4">
          <div class="text-sm font-medium">Trigger Alarm Profile</div>
          <div class="space-y-2">
            <Label for="profile">Profile</Label>
            <select
              id="profile"
              class="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
              bind:value={selectedProfileId}
            >
              {#if alarmConfiguration?.profiles?.length}
                {#each alarmConfiguration.profiles as profile}
                  <option value={profile.id}>{profile.name}</option>
                {/each}
              {:else}
                <option value="">No profiles found</option>
              {/if}
            </select>
          </div>
          <Button
            on:click={handleTriggerProfile}
            disabled={actionBusy || !selectedProfileId}
          >
            Trigger Profile
          </Button>
          <p class="text-xs text-muted-foreground">
            Supports High, Low, UrgentHigh, UrgentLow, ForecastLow.
          </p>
        </div>

        <div class="space-y-3 rounded-lg border border-border bg-muted/30 p-4">
          <div class="text-sm font-medium">Create Fake Entry</div>
          <div class="space-y-2">
            <Label for="sgv">SGV (mg/dL)</Label>
            <Input id="sgv" type="number" bind:value={fakeSgv} min="20" max="600" />
          </div>
          <div class="space-y-2">
            <Label for="device">Device</Label>
            <Input id="device" bind:value={fakeDevice} />
          </div>
          <Button on:click={handleCreateFakeEntry} disabled={actionBusy}>
            Create Entry
          </Button>
        </div>
      </div>

      {#if actionStatus}
        <div class="text-sm text-muted-foreground">{actionStatus}</div>
      {/if}
    </CardContent>
  </Card>

  <Card>
    <CardHeader>
      <CardTitle>Snapshot</CardTitle>
      <CardDescription>Server time: {fetchedAt}</CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      <div class="grid gap-4 md:grid-cols-2">
        <div class="rounded-lg border border-border bg-muted/30 p-4">
          <div class="text-sm font-medium">Latest Entry</div>
          <pre class="mt-2 text-xs leading-relaxed text-muted-foreground">
{latestEntry ? JSON.stringify(latestEntry, null, 2) : "No entry found"}
          </pre>
        </div>
        <div class="rounded-lg border border-border bg-muted/30 p-4">
          <div class="text-sm font-medium">Alarm Configuration</div>
          <pre class="mt-2 text-xs leading-relaxed text-muted-foreground">
{alarmConfiguration ? JSON.stringify(alarmConfiguration, null, 2) : "No alarm configuration found"}
          </pre>
        </div>
      </div>
    </CardContent>
  </Card>
</div>
