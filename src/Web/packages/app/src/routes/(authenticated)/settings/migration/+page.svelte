<script lang="ts">
  import { formatMediumDateTime, formatNumber } from "#lib/utils/formatting.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge, type BadgeVariant } from "#lib/components/ui/badge/index.js";
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import { Progress } from "#lib/components/ui/progress/index.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import CollectionLanes from "#lib/components/migration/CollectionLanes.svelte";
  import TimeRemaining from "#lib/components/migration/TimeRemaining.svelte";
  import Import from "@lucide/svelte/icons/import";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import CheckCircle2 from "@lucide/svelte/icons/circle-check";
  import XCircle from "@lucide/svelte/icons/circle-x";
  import Play from "@lucide/svelte/icons/play";
  import Square from "@lucide/svelte/icons/square";
  import Clock from "@lucide/svelte/icons/clock";
  import Database from "@lucide/svelte/icons/database";
  import Globe from "@lucide/svelte/icons/globe";
  import Server from "@lucide/svelte/icons/server";
  import RefreshCw from "@lucide/svelte/icons/refresh-cw";
  import Info from "@lucide/svelte/icons/info";
  import ArrowRightLeft from "@lucide/svelte/icons/arrow-right-left";
  import { EmptyState } from "#lib/components/shared/index.js";
  import { databaseArtwork } from "#lib/watercolour-icons.js";
  import { watchImport } from "#lib/migration/background-import.svelte.js";
  import * as migrationRemote from "#lib/api/generated/migrations.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import MigrationCompleteMoment from "./MigrationCompleteMoment.svelte";
  import NightscoutTransitionStatus from "./NightscoutTransitionStatus.svelte";
  import { completedCleanly } from "./migration-outcome";
  import {
    type MigrationJobInfo,
    type MigrationJobStatus,
    type PendingMigrationConfig,
    type MigrationSourceDto,
    MigrationJobState,
    MigrationMode,
    MigrationPhase,
  } from "#lib/api/index.js";
  import { fresh } from "#lib/api/fresh-query.js";

  // State
  let activeTab = $state("migrate");
  let loading = $state(true);
  let error = $state<string | null>(null);

  // Pending config and sources
  let pendingConfig = $state<PendingMigrationConfig | null>(null);
  let sources = $state<MigrationSourceDto[]>([]);
  let history = $state<MigrationJobInfo[]>([]);

  // Active migration state
  let activeMigration = $state<MigrationJobStatus | null>(null);
  /** A run this page watched finish cleanly; set only by polling, never by loading history. */
  let completedJobId = $state<string | null>(null);
  let activePoll: { jobId: string } | null = null;
  let destroyed = false;

  // Form state
  let mode = $state<MigrationMode>(MigrationMode.Api);
  let nightscoutUrl = $state("");
  let changingUrl = $state(false);
  let nightscoutApiSecret = $state("");
  let mongoConnectionString = $state("");
  let mongoDatabaseName = $state("");
  let dateRangeStart = $state("");
  let dateRangeEnd = $state("");

  // Form objects
  const testConnectionForm = migrationRemote.testConnection;
  const startMigrationForm = migrationRemote.startMigration;

  // Test connection state
  let connectionTestResult = $state<{
    success: boolean;
    message: string;
    /** The source the test reached; a result for other details says nothing about these. */
    source: string;
  } | null>(null);

  /**
   * The server's address, shown with its path hidden. The form sends no URL until the viewer
   * presses "Change address", and the API then reads the server's own.
   */
  const configuredUrl = $derived(pendingConfig?.nightscoutUrl ?? "");
  const usesConfiguredUrl = $derived(configuredUrl !== "" && !changingUrl);

  const sourceKey = $derived(
    mode === MigrationMode.Api
      ? `api\n${usesConfiguredUrl}\n${nightscoutUrl}\n${nightscoutApiSecret}`
      : `mongo\n${mongoConnectionString}\n${mongoDatabaseName}`
  );
  const shownTestResult = $derived(
    connectionTestResult?.source === sourceKey ? connectionTestResult : null
  );

  // Migration state
  let cancellingMigration = $state(false);

  // Load data
  async function loadData() {
    loading = true;
    error = null;
    try {
      const [configResult, sourcesResult, historyResult] = await Promise.all([
        fresh(migrationRemote.getPendingConfig()),
        fresh(migrationRemote.getSources()),
        fresh(migrationRemote.getHistory()),
      ]);
      if (destroyed) return;

      pendingConfig = configResult;
      sources = sourcesResult || [];
      history = historyResult || [];

      // Pre-populate form if pending config exists
      if (configResult?.hasPendingConfig) {
        if (configResult.mode !== undefined && configResult.mode !== null) {
          mode = configResult.mode;
        }
        if (configResult.mongoDatabaseName) {
          mongoDatabaseName = configResult.mongoDatabaseName;
        }
      }

      // Check for active migrations
      const runningJob = historyResult?.find(
        (j) =>
          j.state === MigrationJobState.Running ||
          j.state === MigrationJobState.Pending
      );
      if (runningJob?.id) {
        void pollMigrationStatus(runningJob.id);
      }
    } catch (err) {
      console.error("Failed to load migration data:", err);
      error = remoteErrorMessage(err, "Failed to load migration data");
    } finally {
      loading = false;
    }
  }

  // Reloads what a finished run changes. The page stays up, and the form keeps what was typed.
  async function refreshLists() {
    try {
      const [sourcesResult, historyResult] = await Promise.all([
        fresh(migrationRemote.getSources()),
        fresh(migrationRemote.getHistory()),
      ]);
      if (destroyed) return;
      sources = sourcesResult || [];
      history = historyResult || [];
    } catch (err) {
      error = remoteErrorMessage(err, "Failed to load migration data");
    }
  }

  // Initial load. Deferred so this effect does not track what the load reads.
  $effect(() => {
    queueMicrotask(loadData);
    return () => {
      destroyed = true;
      activePoll = null;
    };
  });

  // Poll migration status
  async function pollMigrationStatus(jobId: string) {
    if (destroyed || activePoll?.jobId === jobId) return;
    const poll = { jobId };
    activePoll = poll;
    completedJobId = null;
    // The run continues on the server whatever a status read does, so one failed read is
    // retried and only a run of them is reported.
    const MAX_CONSECUTIVE_FAILURES = 5;
    let consecutiveFailures = 0;
    while (activePoll === poll) {
      try {
        const status = await fresh(migrationRemote.getStatus(jobId));
        if (activePoll !== poll) return;
        consecutiveFailures = 0;
        activeMigration = status;

        if (!isLive(status)) {
          activePoll = null;
          await refreshLists();
          if (completedCleanly(history.find((j) => j.id === jobId))) completedJobId = jobId;
          return;
        }
      } catch (err) {
        if (activePoll !== poll) return;
        if (++consecutiveFailures >= MAX_CONSECUTIVE_FAILURES) {
          console.error("Failed to poll migration status:", err);
          activePoll = null;
          activeMigration = null;
          error = remoteErrorMessage(err, "Failed to read migration status");
          return;
        }
      }

      await new Promise((resolve) => setTimeout(resolve, 2000));
    }
  }

  function isLive(status: MigrationJobStatus | null): boolean {
    return (
      status?.state === MigrationJobState.Pending ||
      status?.state === MigrationJobState.Running
    );
  }

  // Cancel migration
  async function handleCancelMigration() {
    if (!activeMigration?.jobId) return;
    const jobId = activeMigration.jobId;
    cancellingMigration = true;
    try {
      await migrationRemote.cancelMigration(jobId);
      activePoll = null;
      // The cancel is done whether or not its status can be read back.
      activeMigration = await fresh(migrationRemote.getStatus(jobId)).catch(() => ({
        ...activeMigration,
        state: MigrationJobState.Cancelled,
      }));
      await refreshLists();
    } catch (err) {
      error = describeSubmitError(err, "Failed to cancel migration");
    } finally {
      cancellingMigration = false;
    }
  }

  function getStateBadge(state: MigrationJobState | undefined): {
    variant: BadgeVariant;
    label: string;
  } {
    switch (state) {
      case MigrationJobState.Pending:
        return { variant: "secondary", label: "Pending" };
      case MigrationJobState.Running:
        return { variant: "default", label: "Running" };
      case MigrationJobState.Completed:
        return { variant: "success", label: "Completed" };
      case MigrationJobState.Failed:
        return { variant: "destructive", label: "Failed" };
      case MigrationJobState.Cancelled:
        return { variant: "outline", label: "Canceled" };
      case MigrationJobState.Interrupted:
        return { variant: "destructive", label: "Interrupted" };
      default:
        return { variant: "secondary", label: "Unknown" };
    }
  }

  // Derived: has active migration
  const hasActiveMigration = $derived(isLive(activeMigration));

  // A date input gives a calendar day. The range runs from the start of the first day to the
  // end of the last, in the browser's time zone.
  const rangeStart = $derived(dateRangeStart ? new Date(`${dateRangeStart}T00:00:00`) : null);
  const rangeEnd = $derived(dateRangeEnd ? new Date(`${dateRangeEnd}T23:59:59.999`) : null);
  const isRangeValid = $derived(!rangeStart || !rangeEnd || rangeStart <= rangeEnd);

  // Nightscout can be readable without an API secret, so the secret is optional.
  const isFormValid = $derived(
    (mode === MigrationMode.Api
      ? usesConfiguredUrl || nightscoutUrl.trim() !== ""
      : mongoConnectionString.trim() !== "" && mongoDatabaseName.trim() !== "") && isRangeValid
  );
</script>

<svelte:head>
  <title>Move from Nightscout - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
  <!-- Header -->
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
      <Import class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Move from Nightscout</h1>
      <p class="text-muted-foreground">
        Import your data from Nightscout or MongoDB. Then see when it is safe to stop using Nightscout.
      </p>
    </div>
  </div>

  {#if loading}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else if error}
    <Alert.Root variant="destructive" class="mb-6">
      <AlertTriangle class="h-4 w-4" />
      <Alert.Title>Error</Alert.Title>
      <Alert.Description>{error}</Alert.Description>
    </Alert.Root>
  {/if}

  {#if !loading}
    <!-- Pending Migration Notice -->
    {#if pendingConfig?.hasPendingConfig}
      <Alert.Root class="mb-6">
        <Info class="h-4 w-4" />
        <Alert.Title>Migration settings found</Alert.Title>
        <Alert.Description>
          The server settings give a migration source, and the form shows them. Check them, then start the migration.
        </Alert.Description>
      </Alert.Root>
    {/if}

    {#if completedJobId}
      {#key completedJobId}
        <MigrationCompleteMoment />
      {/key}
    {/if}

    <Tabs.Root bind:value={activeTab} class="space-y-6">
      <Tabs.List variant="wrap">
        <Tabs.Trigger value="migrate">
          <Play class="h-4 w-4" />
          New migration
        </Tabs.Trigger>
        <Tabs.Trigger value="progress">
          <RefreshCw class="h-4 w-4" />
          Progress
          {#if hasActiveMigration}
            <Badge variant="default" live class="ml-1">Active</Badge>
          {/if}
        </Tabs.Trigger>
        <Tabs.Trigger value="history">
          <Clock class="h-4 w-4" />
          History
          {#if history.length > 0}
            <Badge variant="secondary" class="ml-1">{history.length}</Badge>
          {/if}
        </Tabs.Trigger>
        <Tabs.Trigger value="status">
          <ArrowRightLeft class="h-4 w-4" />
          Status
        </Tabs.Trigger>
      </Tabs.List>

      <!-- New Migration Tab -->
      <Tabs.Content value="migrate">
        <Card>
          <CardHeader>
            <CardTitle>Start new migration</CardTitle>
            <CardDescription>
              Import the history of a Nightscout site: entries, treatments,
              device statuses, profiles, foods, and activities.
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-6">
            <!-- Mode Selector -->
            <div class="space-y-3">
              <Label>Migration source</Label>
              <RadioGroup.Root
                value={String(mode)}
                onValueChange={(value) => (mode = Number(value))}
                class="flex flex-wrap gap-4"
              >
                <div class="flex items-center space-x-2">
                  <RadioGroup.Item value={String(MigrationMode.Api)} id="mode-api" />
                  <Label for="mode-api" class="flex items-center">
                    <Globe class="h-4 w-4" />
                    Nightscout API
                  </Label>
                </div>
                <div class="flex items-center space-x-2">
                  <RadioGroup.Item value={String(MigrationMode.MongoDb)} id="mode-mongodb" />
                  <Label for="mode-mongodb" class="flex items-center">
                    <Database class="h-4 w-4" />
                    MongoDB (advanced)
                  </Label>
                </div>
              </RadioGroup.Root>
            </div>

            <!-- API Mode Fields -->
            {#if mode === MigrationMode.Api}
              <div class="grid gap-4 @lg:grid-cols-2">
                <div class="space-y-2">
                  <Label for={usesConfiguredUrl ? undefined : "nightscout-url"}>Nightscout URL</Label>
                  {#if usesConfiguredUrl}
                    <div class="flex flex-wrap items-center justify-between gap-2">
                      <span class="min-w-0 break-all text-sm" data-testid="configured-nightscout-url">
                        {configuredUrl}
                      </span>
                      <Button variant="outline" size="sm" onclick={() => (changingUrl = true)}>
                        Change address
                      </Button>
                    </div>
                  {:else}
                    <div class="flex flex-wrap items-center gap-2">
                      <Input
                        id="nightscout-url"
                        placeholder="https://your-nightscout.herokuapp.com"
                        class="min-w-0 flex-1"
                        bind:value={nightscoutUrl}
                      />
                      {#if configuredUrl}
                        <Button
                          variant="ghost"
                          size="sm"
                          onclick={() => {
                            changingUrl = false;
                            nightscoutUrl = "";
                          }}
                        >
                          Keep current address
                        </Button>
                      {/if}
                    </div>
                  {/if}
                </div>
                <div class="space-y-2">
                  <Label for="api-secret">
                    API secret
                    <span class="font-normal text-muted-foreground">(optional)</span>
                  </Label>
                  <Input
                    id="api-secret"
                    type="password"
                    placeholder="Leave blank if the site needs none"
                    bind:value={nightscoutApiSecret}
                  />
                </div>
              </div>
            {:else}
              <!-- MongoDB Mode Fields -->
              <div class="space-y-4">
                <div class="space-y-2">
                  <Label for="mongo-connection">
                    MongoDB connection string
                  </Label>
                  <Input
                    id="mongo-connection"
                    type="password"
                    placeholder="mongodb+srv://user:pass@cluster.mongodb.net"
                    bind:value={mongoConnectionString}
                  />
                </div>
                <div class="space-y-2">
                  <Label for="mongo-database">Database name</Label>
                  <Input
                    id="mongo-database"
                    placeholder="nightscout"
                    bind:value={mongoDatabaseName}
                  />
                </div>
              </div>
            {/if}

            <!-- Date Range -->
            <div class="space-y-3">
              <Label>Date range (optional)</Label>
              <p class="text-sm text-muted-foreground">
                Leave the dates empty to import all data.
              </p>
              <div class="grid gap-4 @lg:grid-cols-2">
                <div class="space-y-2">
                  <Label for="date-start">Start date</Label>
                  <Input
                    id="date-start"
                    type="date"
                    bind:value={dateRangeStart}
                  />
                </div>
                <div class="space-y-2">
                  <Label for="date-end">End date</Label>
                  <Input id="date-end" type="date" bind:value={dateRangeEnd} />
                </div>
              </div>
              {#if !isRangeValid}
                <p class="text-sm text-destructive">
                  The end date is before the start date.
                </p>
              {/if}
            </div>

            <!-- Connection Test Result -->
            {#if shownTestResult}
              <Alert.Root
                variant={shownTestResult.success ? "default" : "destructive"}
              >
                {#if shownTestResult.success}
                  <CheckCircle2 class="h-4 w-4" />
                {:else}
                  <XCircle class="h-4 w-4" />
                {/if}
                <Alert.Title>
                  {shownTestResult.success
                    ? "Connection successful"
                    : "Connection failed"}
                </Alert.Title>
                <Alert.Description>
                  {shownTestResult.message}
                </Alert.Description>
              </Alert.Root>
            {/if}

            <!-- Actions -->
            <div class="flex flex-col gap-3">
              <form
                {...testConnectionForm.enhance(async ({ submit }) => {
                  const source = sourceKey;
                  connectionTestResult = null;
                  try {
                    await submit();
                  } catch (err) {
                    connectionTestResult = {
                      success: false,
                      message: describeSubmitError(err, "Connection test failed"),
                      source,
                    };
                    return;
                  }
                  const result = testConnectionForm.result;
                  // Only a MongoDB test counts documents.
                  const counted =
                    result?.entryCount != null && result.treatmentCount != null
                      ? `Connected. Found ${formatNumber(result.entryCount)} entries and ${formatNumber(result.treatmentCount)} treatments.`
                      : "Connected.";
                  connectionTestResult = {
                    success: result?.isSuccess || false,
                    message: result?.isSuccess
                      ? counted
                      : result?.errorMessage || "Connection test failed",
                    source,
                  };
                })}
              >
                <input {...testConnectionForm.fields.mode.as("hidden", mode)} />
                {#if mode === MigrationMode.Api}
                  {#if !usesConfiguredUrl}
                    <input {...testConnectionForm.fields.nightscoutUrl.as("hidden", nightscoutUrl)} />
                  {/if}
                  <input {...testConnectionForm.fields.nightscoutApiSecret.as("hidden", nightscoutApiSecret)} />
                {:else}
                  <input {...testConnectionForm.fields.mongoConnectionString.as("hidden", mongoConnectionString)} />
                  <input {...testConnectionForm.fields.mongoDatabaseName.as("hidden", mongoDatabaseName)} />
                {/if}
                <Button
                  type="submit"
                  variant="outline"
                  disabled={!isFormValid ||
                    !!testConnectionForm.pending ||
                    !!startMigrationForm.pending}
                >
                  {#if testConnectionForm.pending}
                    <Loader2 class="h-4 w-4 mr-2 animate-spin" />
                  {:else}
                    <Server class="h-4 w-4 mr-2" />
                  {/if}
                  Test connection
                </Button>
              </form>
              <form
                {...startMigrationForm.enhance(async ({ submit }) => {
                  error = null;
                  try {
                    await submit();
                  } catch (err) {
                    error = describeSubmitError(err, "Failed to start migration");
                    return;
                  }
                  const jobInfo = startMigrationForm.result;
                  if (jobInfo?.id) {
                    activeTab = "progress";
                    // This page shows the run, so the app keeps only its banner for it.
                    watchImport(jobInfo.id, { acknowledged: true });
                    void pollMigrationStatus(jobInfo.id);
                  } else {
                    error = "Failed to start migration";
                  }
                })}
              >
                <input {...startMigrationForm.fields.mode.as("hidden", mode)} />
                {#if mode === MigrationMode.Api}
                  {#if !usesConfiguredUrl}
                    <input {...startMigrationForm.fields.nightscoutUrl.as("hidden", nightscoutUrl)} />
                  {/if}
                  <input {...startMigrationForm.fields.nightscoutApiSecret.as("hidden", nightscoutApiSecret)} />
                {:else}
                  <input {...startMigrationForm.fields.mongoConnectionString.as("hidden", mongoConnectionString)} />
                  <input {...startMigrationForm.fields.mongoDatabaseName.as("hidden", mongoDatabaseName)} />
                {/if}
                {#if rangeStart}
                  <input {...startMigrationForm.fields.startDate.as("hidden", rangeStart.toISOString())} />
                {/if}
                {#if rangeEnd}
                  <input {...startMigrationForm.fields.endDate.as("hidden", rangeEnd.toISOString())} />
                {/if}
                <Button
                  type="submit"
                  size="xl"
                  class="w-full"
                  disabled={!isFormValid ||
                    !shownTestResult?.success ||
                    !!startMigrationForm.pending ||
                    !!hasActiveMigration}
                >
                  {#if startMigrationForm.pending}
                    <Loader2 class="animate-spin" />
                  {:else}
                    <Play />
                  {/if}
                  Start migration
                </Button>
              </form>
            </div>
          </CardContent>
        </Card>
      </Tabs.Content>

      <!-- Progress Tab -->
      <Tabs.Content value="progress">
        <Card>
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              Active migration
              {#if hasActiveMigration}
                <Badge variant="default" live>Running</Badge>
              {/if}
            </CardTitle>
          </CardHeader>
          <CardContent>
            {#if activeMigration && hasActiveMigration}
              <div class="space-y-6">
                <!-- Progress Bar -->
                <div class="space-y-2">
                  <div class="flex justify-between text-sm">
                    <span class="text-muted-foreground">
                      {#if activeMigration.phase === MigrationPhase.Counting}
                        Counting the records in the source
                      {:else if activeMigration.phase === MigrationPhase.Importing}
                        <TimeRemaining remaining={activeMigration.estimatedTimeRemaining} />
                      {:else}
                        Connecting to the source
                      {/if}
                    </span>
                    <span class="font-medium">
                      {Math.round(activeMigration.progressPercentage ?? 0)}%
                    </span>
                  </div>
                  <Progress value={activeMigration.progressPercentage ?? 0} />
                </div>

                {#if activeMigration.collectionProgress && Object.keys(activeMigration.collectionProgress).length > 0}
                  <CollectionLanes
                    collections={activeMigration.collectionProgress}
                    currentCollection={activeMigration.currentCollection}
                  />
                {/if}

                <!-- Cancel Button -->
                <Button
                  variant="destructive"
                  onclick={handleCancelMigration}
                  disabled={cancellingMigration}
                >
                  {#if cancellingMigration}
                    <Loader2 class="h-4 w-4 mr-2 animate-spin" />
                  {:else}
                    <Square class="h-4 w-4 mr-2" />
                  {/if}
                  Cancel migration
                </Button>
              </div>
            {:else if activeMigration}
              {@const badge = getStateBadge(activeMigration.state)}
              <!-- The run this page watched, after it ended -->
              <div class="space-y-4">
                <div class="flex items-center gap-2">
                  <span class="font-medium">Last migration</span>
                  <Badge variant={badge.variant}>{badge.label}</Badge>
                </div>
                {#if activeMigration.errorMessage}
                  <p
                    class="text-sm {activeMigration.hasFailures
                      ? 'text-destructive'
                      : 'text-muted-foreground'}"
                  >
                    {activeMigration.errorMessage}
                  </p>
                {/if}
                {#if activeMigration.collectionProgress && Object.keys(activeMigration.collectionProgress).length > 0}
                  <CollectionLanes collections={activeMigration.collectionProgress} />
                {/if}
              </div>
            {:else}
              <EmptyState
                art={databaseArtwork}
                title="No active migration"
                body="Start a new migration to see progress here"
              />
            {/if}
          </CardContent>
        </Card>
      </Tabs.Content>

      <!-- History Tab -->
      <Tabs.Content value="history">
        <Card>
          <CardHeader>
            <CardTitle>Migration history</CardTitle>
          </CardHeader>
          <CardContent>
            {#if history.length === 0}
              <EmptyState
                art={databaseArtwork}
                title="No migration history"
                body="Completed migrations will appear here"
              />
            {:else}
              <ul class="-my-3 divide-y">
                {#each history as job (job.id)}
                  {@const badge = getStateBadge(job.state)}
                  <li class="flex items-start gap-3 py-3">
                    {#if job.mode === MigrationMode.MongoDb}
                      <Database class="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                    {:else}
                      <Globe class="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                    {/if}
                    <div class="min-w-0 flex-1 space-y-1">
                      <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
                        <span class="min-w-0 font-medium break-all">
                          {job.sourceDescription || "Unknown source"}
                        </span>
                        <Badge variant={badge.variant}>
                          {badge.label}
                        </Badge>
                      </div>
                      <p class="text-sm text-muted-foreground">
                        Started: {formatMediumDateTime(job.startedAt)}
                      </p>
                      {#if job.completedAt}
                        <p class="text-sm text-muted-foreground">
                          {#if job.state === MigrationJobState.Completed}
                            Completed: {formatMediumDateTime(job.completedAt)}
                          {:else}
                            Ended: {formatMediumDateTime(job.completedAt)}
                          {/if}
                        </p>
                      {/if}
                      {#if job.errorMessage}
                        <p
                          class="text-sm {job.hasFailures ||
                          job.state === MigrationJobState.Failed ||
                          job.state === MigrationJobState.Interrupted ||
                          job.state === MigrationJobState.Cancelled
                            ? 'text-destructive'
                            : 'text-muted-foreground'}"
                        >
                          {job.errorMessage}
                        </p>
                      {/if}
                    </div>
                  </li>
                {/each}
              </ul>
            {/if}
          </CardContent>
        </Card>

        <!-- Sources Section -->
        {#if sources.length > 0}
          <Card class="mt-6">
            <CardHeader>
              <CardTitle>Known sources</CardTitle>
            </CardHeader>
            <CardContent>
              <ul class="-my-3 divide-y">
                {#each sources as source (source.id)}
                  <li class="flex items-start gap-3 py-3">
                    {#if source.mode === MigrationMode.MongoDb}
                      <Database class="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                    {:else}
                      <Globe class="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                    {/if}
                    <div class="min-w-0 flex-1 space-y-1">
                      <p class="font-medium break-all">
                        {source.nightscoutUrl || source.mongoDatabaseName || "Unknown"}
                      </p>
                      <p class="text-sm text-muted-foreground">
                        Last migration: {formatMediumDateTime(source.lastMigrationAt)}
                      </p>
                    </div>
                  </li>
                {/each}
              </ul>
            </CardContent>
          </Card>
        {/if}
      </Tabs.Content>

      <Tabs.Content value="status">
        <NightscoutTransitionStatus />
      </Tabs.Content>
    </Tabs.Root>
  {/if}
</div>
