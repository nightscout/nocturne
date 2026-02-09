<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Tabs from "$lib/components/ui/tabs";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as RadioGroup from "$lib/components/ui/radio-group";
  import { Progress } from "$lib/components/ui/progress";
  import * as Alert from "$lib/components/ui/alert";
  import {
    Import,
    Loader2,
    AlertTriangle,
    CheckCircle2,
    XCircle,
    Play,
    Square,
    Clock,
    Database,
    Globe,
    Server,
    RefreshCw,
    Info,
  } from "lucide-svelte";
  import * as migrationRemote from "$lib/data/generated/migrations.generated.remote";
  import {
    type MigrationJobInfo,
    type MigrationJobStatus,
    type PendingMigrationConfig,
    type MigrationSourceDto,
    MigrationJobState,
    MigrationMode,
  } from "$api";

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
  let pollingActive = $state(false);

  // Form state
  let mode = $state<"Api" | "MongoDb">("Api");
  let nightscoutUrl = $state("");
  let nightscoutApiSecret = $state("");
  let mongoConnectionString = $state("");
  let mongoDatabaseName = $state("");
  let dateRangeStart = $state("");
  let dateRangeEnd = $state("");

  // Test connection state
  let testingConnection = $state(false);
  let connectionTestResult = $state<{
    success: boolean;
    message: string;
    entryCount?: number;
    treatmentCount?: number;
  } | null>(null);

  // Migration state
  let startingMigration = $state(false);
  let cancellingMigration = $state(false);

  // Load data
  async function loadData() {
    loading = true;
    error = null;
    try {
      const [configResult, sourcesResult, historyResult] = await Promise.all([
        migrationRemote.getPendingConfig(),
        migrationRemote.getSources(),
        migrationRemote.getHistory(),
      ]);

      pendingConfig = configResult;
      sources = sourcesResult || [];
      history = historyResult || [];

      // Pre-populate form if pending config exists
      if (configResult?.hasPendingConfig) {
        if (configResult.mode !== undefined && configResult.mode !== null) {
          mode =
            configResult.mode === MigrationMode.MongoDb ? "MongoDb" : "Api";
        }
        if (configResult.nightscoutUrl) {
          nightscoutUrl = configResult.nightscoutUrl;
        }
        if (configResult.mongoDatabaseName) {
          mongoDatabaseName = configResult.mongoDatabaseName;
        }
      }

      // Check for active migrations
      const runningJob = historyResult?.find(
        (j) =>
          j.state === MigrationJobState.Running ||
          j.state === MigrationJobState.Pending ||
          j.state === MigrationJobState.Validating
      );
      if (runningJob?.id) {
        await pollMigrationStatus(runningJob.id);
      }
    } catch (err) {
      console.error("Failed to load migration data:", err);
      error = "Failed to load migration data";
    } finally {
      loading = false;
    }
  }

  // Initial load
  $effect(() => {
    loadData();
  });

  // Test connection
  async function handleTestConnection() {
    testingConnection = true;
    connectionTestResult = null;
    try {
      const result = await migrationRemote.testConnection({
        mode: mode,
        nightscoutUrl: mode === "Api" ? nightscoutUrl : undefined,
        nightscoutApiSecret: mode === "Api" ? nightscoutApiSecret : undefined,
        mongoConnectionString:
          mode === "MongoDb" ? mongoConnectionString : undefined,
        mongoDatabaseName: mode === "MongoDb" ? mongoDatabaseName : undefined,
      });
      connectionTestResult = {
        success: result.isSuccess || false,
        message: result.isSuccess
          ? `Connected successfully! Found ${result.entryCount ?? 0} entries and ${result.treatmentCount ?? 0} treatments.`
          : result.errorMessage || "Connection failed",
        entryCount: result.entryCount ?? undefined,
        treatmentCount: result.treatmentCount ?? undefined,
      };
    } catch (err) {
      connectionTestResult = {
        success: false,
        message: err instanceof Error ? err.message : "Connection test failed",
      };
    } finally {
      testingConnection = false;
    }
  }

  // Start migration
  async function handleStartMigration() {
    startingMigration = true;
    try {
      const jobInfo = await migrationRemote.startMigration({
        mode: mode,
        nightscoutUrl: mode === "Api" ? nightscoutUrl : undefined,
        nightscoutApiSecret: mode === "Api" ? nightscoutApiSecret : undefined,
        mongoConnectionString:
          mode === "MongoDb" ? mongoConnectionString : undefined,
        mongoDatabaseName: mode === "MongoDb" ? mongoDatabaseName : undefined,
        startDate: dateRangeStart ? new Date(dateRangeStart).toISOString() : undefined,
        endDate: dateRangeEnd ? new Date(dateRangeEnd).toISOString() : undefined,
        collections: ["entries", "treatments"],
      });

      if (jobInfo?.id) {
        await pollMigrationStatus(jobInfo.id);
        activeTab = "progress";
      }
    } catch (err) {
      error = err instanceof Error ? err.message : "Failed to start migration";
    } finally {
      startingMigration = false;
    }
  }

  // Poll migration status
  async function pollMigrationStatus(jobId: string) {
    pollingActive = true;
    try {
      while (pollingActive) {
        const status = await migrationRemote.getStatus(jobId);
        activeMigration = status;

        // Check if completed
        if (
          status.state === MigrationJobState.Completed ||
          status.state === MigrationJobState.Failed ||
          status.state === MigrationJobState.Cancelled
        ) {
          pollingActive = false;
          await loadData(); // Refresh history
          break;
        }

        // Wait before next poll
        await new Promise((resolve) => setTimeout(resolve, 2000));
      }
    } catch (err) {
      console.error("Failed to poll migration status:", err);
      pollingActive = false;
    }
  }

  // Cancel migration
  async function handleCancelMigration() {
    if (!activeMigration?.jobId) return;
    cancellingMigration = true;
    try {
      await migrationRemote.cancelMigration(activeMigration.jobId);
      pollingActive = false;
      await loadData();
    } catch (err) {
      error = err instanceof Error ? err.message : "Failed to cancel migration";
    } finally {
      cancellingMigration = false;
    }
  }

  // Format date
  function formatDate(dateStr: Date | string | undefined): string {
    if (!dateStr) return "N/A";
    const date = new Date(dateStr);
    return date.toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
      year: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  // Get state badge
  function getStateBadge(state: MigrationJobState | undefined) {
    switch (state) {
      case MigrationJobState.Pending:
        return { variant: "secondary", label: "Pending" };
      case MigrationJobState.Validating:
        return { variant: "secondary", label: "Validating" };
      case MigrationJobState.Running:
        return { variant: "default", label: "Running" };
      case MigrationJobState.Completed:
        return { variant: "success", label: "Completed" };
      case MigrationJobState.Failed:
        return { variant: "destructive", label: "Failed" };
      case MigrationJobState.Cancelled:
        return { variant: "outline", label: "Cancelled" };
      default:
        return { variant: "secondary", label: "Unknown" };
    }
  }

  // Derived: has active migration
  const hasActiveMigration = $derived(
    activeMigration &&
      (activeMigration.state === MigrationJobState.Pending ||
        activeMigration.state === MigrationJobState.Validating ||
        activeMigration.state === MigrationJobState.Running)
  );

  // Derived: form valid
  const isFormValid = $derived(
    mode === "Api"
      ? nightscoutUrl.trim() !== "" && nightscoutApiSecret.trim() !== ""
      : mongoConnectionString.trim() !== "" && mongoDatabaseName.trim() !== ""
  );
</script>

<svelte:head>
  <title>Data Migration - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-4xl">
  <!-- Header -->
  <div class="mb-8">
    <div class="flex items-center gap-3 mb-2">
      <div
        class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
      >
        <Import class="h-5 w-5 text-primary" />
      </div>
      <div>
        <h1 class="text-3xl font-bold tracking-tight">Data Migration</h1>
        <p class="text-muted-foreground">
          Import your data from Nightscout or MongoDB
        </p>
      </div>
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
        <Alert.Title>Migration Configuration Detected</Alert.Title>
        <Alert.Description>
          Your deployment is configured for migration. The form below has been
          pre-populated with your settings. Review and start the migration when
          ready.
        </Alert.Description>
      </Alert.Root>
    {/if}

    <Tabs.Root bind:value={activeTab} class="space-y-6">
      <Tabs.List class="grid w-full grid-cols-3">
        <Tabs.Trigger value="migrate" class="gap-2">
          <Play class="h-4 w-4" />
          New Migration
        </Tabs.Trigger>
        <Tabs.Trigger value="progress" class="gap-2">
          <RefreshCw class="h-4 w-4" />
          Progress
          {#if hasActiveMigration}
            <Badge variant="default" class="ml-1 animate-pulse">Active</Badge>
          {/if}
        </Tabs.Trigger>
        <Tabs.Trigger value="history" class="gap-2">
          <Clock class="h-4 w-4" />
          History
          {#if history.length > 0}
            <Badge variant="secondary" class="ml-1">{history.length}</Badge>
          {/if}
        </Tabs.Trigger>
      </Tabs.List>

      <!-- New Migration Tab -->
      <Tabs.Content value="migrate">
        <Card>
          <CardHeader>
            <CardTitle>Start New Migration</CardTitle>
            <CardDescription>
              Import entries and treatments from an existing Nightscout
              instance.
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-6">
            <!-- Mode Selector -->
            <div class="space-y-3">
              <Label>Migration Source</Label>
              <RadioGroup.Root bind:value={mode} class="flex gap-4">
                <div class="flex items-center space-x-2">
                  <RadioGroup.Item value="Api" id="mode-api" />
                  <Label for="mode-api" class="flex items-center gap-2">
                    <Globe class="h-4 w-4" />
                    Nightscout API
                  </Label>
                </div>
                <div class="flex items-center space-x-2">
                  <RadioGroup.Item value="MongoDb" id="mode-mongodb" />
                  <Label for="mode-mongodb" class="flex items-center gap-2">
                    <Database class="h-4 w-4" />
                    MongoDB (Advanced)
                  </Label>
                </div>
              </RadioGroup.Root>
            </div>

            <!-- API Mode Fields -->
            {#if mode === "Api"}
              <div class="grid gap-4 md:grid-cols-2">
                <div class="space-y-2">
                  <Label for="nightscout-url">Nightscout URL</Label>
                  <Input
                    id="nightscout-url"
                    placeholder="https://your-nightscout.herokuapp.com"
                    bind:value={nightscoutUrl}
                  />
                </div>
                <div class="space-y-2">
                  <Label for="api-secret">API Secret</Label>
                  <Input
                    id="api-secret"
                    type="password"
                    placeholder="Your API secret"
                    bind:value={nightscoutApiSecret}
                  />
                </div>
              </div>
            {:else}
              <!-- MongoDB Mode Fields -->
              <div class="space-y-4">
                <div class="space-y-2">
                  <Label for="mongo-connection">
                    MongoDB Connection String
                  </Label>
                  <Input
                    id="mongo-connection"
                    type="password"
                    placeholder="mongodb+srv://user:pass@cluster.mongodb.net"
                    bind:value={mongoConnectionString}
                  />
                </div>
                <div class="space-y-2">
                  <Label for="mongo-database">Database Name</Label>
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
              <Label>Date Range (Optional)</Label>
              <p class="text-sm text-muted-foreground">
                Leave empty to import all data, or specify a range to import
                partial data.
              </p>
              <div class="grid gap-4 md:grid-cols-2">
                <div class="space-y-2">
                  <Label for="date-start">Start Date</Label>
                  <Input
                    id="date-start"
                    type="date"
                    bind:value={dateRangeStart}
                  />
                </div>
                <div class="space-y-2">
                  <Label for="date-end">End Date</Label>
                  <Input id="date-end" type="date" bind:value={dateRangeEnd} />
                </div>
              </div>
            </div>

            <!-- Connection Test Result -->
            {#if connectionTestResult}
              <Alert.Root
                variant={connectionTestResult.success
                  ? "default"
                  : "destructive"}
              >
                {#if connectionTestResult.success}
                  <CheckCircle2 class="h-4 w-4" />
                {:else}
                  <XCircle class="h-4 w-4" />
                {/if}
                <Alert.Title>
                  {connectionTestResult.success
                    ? "Connection Successful"
                    : "Connection Failed"}
                </Alert.Title>
                <Alert.Description>
                  {connectionTestResult.message}
                </Alert.Description>
              </Alert.Root>
            {/if}

            <!-- Actions -->
            <div class="flex gap-3">
              <Button
                variant="outline"
                onclick={handleTestConnection}
                disabled={!isFormValid ||
                  testingConnection ||
                  startingMigration}
              >
                {#if testingConnection}
                  <Loader2 class="h-4 w-4 mr-2 animate-spin" />
                {:else}
                  <Server class="h-4 w-4 mr-2" />
                {/if}
                Test Connection
              </Button>
              <Button
                onclick={handleStartMigration}
                disabled={!isFormValid ||
                  !connectionTestResult?.success ||
                  startingMigration ||
                  hasActiveMigration}
              >
                {#if startingMigration}
                  <Loader2 class="h-4 w-4 mr-2 animate-spin" />
                {:else}
                  <Play class="h-4 w-4 mr-2" />
                {/if}
                Start Migration
              </Button>
            </div>
          </CardContent>
        </Card>
      </Tabs.Content>

      <!-- Progress Tab -->
      <Tabs.Content value="progress">
        <Card>
          <CardHeader>
            <CardTitle class="flex items-center gap-2">
              Active Migration
              {#if hasActiveMigration}
                <Badge variant="default" class="animate-pulse">Running</Badge>
              {/if}
            </CardTitle>
            <CardDescription>
              Monitor the progress of your current migration.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {#if activeMigration && hasActiveMigration}
              <div class="space-y-6">
                <!-- Progress Bar -->
                <div class="space-y-2">
                  <div class="flex justify-between text-sm">
                    <span class="text-muted-foreground">Progress</span>
                    <span class="font-medium">
                      {activeMigration.progressPercentage ?? 0}%
                    </span>
                  </div>
                  <Progress value={activeMigration.progressPercentage ?? 0} />
                </div>

                <!-- Current Operation -->
                {#if activeMigration.currentOperation}
                  <div class="p-4 bg-muted rounded-lg">
                    <p class="text-sm text-muted-foreground mb-1">
                      Current Operation
                    </p>
                    <p class="font-medium">
                      {activeMigration.currentOperation}
                    </p>
                  </div>
                {/if}

                <!-- Collection Progress -->
                {#if activeMigration.collectionProgress && Object.keys(activeMigration.collectionProgress).length > 0}
                  <div class="space-y-3">
                    <Label>Collection Progress</Label>
                    {#each Object.entries(activeMigration.collectionProgress) as [name, collection]}
                      <div
                        class="flex justify-between items-center p-3 border rounded-lg"
                      >
                        <span class="font-medium capitalize">
                          {name}
                        </span>
                        <div class="text-right">
                          <span class="text-sm text-muted-foreground">
                            {collection.documentsMigrated} / {collection.totalDocuments}
                          </span>
                        </div>
                      </div>
                    {/each}
                  </div>
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
                  Cancel Migration
                </Button>
              </div>
            {:else}
              <div class="text-center py-12 text-muted-foreground">
                <RefreshCw class="h-12 w-12 mx-auto mb-3 opacity-50" />
                <p>No active migration</p>
                <p class="text-sm">
                  Start a new migration to see progress here
                </p>
              </div>
            {/if}
          </CardContent>
        </Card>
      </Tabs.Content>

      <!-- History Tab -->
      <Tabs.Content value="history">
        <Card>
          <CardHeader>
            <CardTitle>Migration History</CardTitle>
            <CardDescription>
              View past migrations and their results.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {#if history.length === 0}
              <div class="text-center py-12 text-muted-foreground">
                <Clock class="h-12 w-12 mx-auto mb-3 opacity-50" />
                <p>No migration history</p>
                <p class="text-sm">Completed migrations will appear here</p>
              </div>
            {:else}
              <div class="space-y-3">
                {#each history as job}
                  {@const badge = getStateBadge(job.state)}
                  <div
                    class="flex items-center justify-between p-4 rounded-lg border"
                  >
                    <div class="flex items-center gap-3">
                      <div class="p-2 rounded-lg bg-muted">
                        {#if job.mode === MigrationMode.MongoDb}
                          <Database class="h-5 w-5" />
                        {:else}
                          <Globe class="h-5 w-5" />
                        {/if}
                      </div>
                      <div>
                        <div class="font-medium flex items-center gap-2">
                          {job.sourceDescription || "Unknown Source"}
                          <Badge variant={badge.variant as any}>
                            {badge.label}
                          </Badge>
                        </div>
                        <div class="text-sm text-muted-foreground">
                          Started: {formatDate(job.startedAt)}
                        </div>
                        {#if job.errorMessage}
                          <div class="text-sm text-destructive mt-1">
                            {job.errorMessage}
                          </div>
                        {/if}
                      </div>
                    </div>
                    <div class="text-right text-sm text-muted-foreground">
                      {#if job.completedAt}
                        <div>Completed: {formatDate(job.completedAt)}</div>
                      {/if}
                    </div>
                  </div>
                {/each}
              </div>
            {/if}
          </CardContent>
        </Card>

        <!-- Sources Section -->
        {#if sources.length > 0}
          <Card class="mt-6">
            <CardHeader>
              <CardTitle>Known Sources</CardTitle>
              <CardDescription>
                Previously used migration sources with their last sync
                timestamps.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <div class="space-y-3">
                {#each sources as source}
                  <div
                    class="flex items-center justify-between p-4 rounded-lg border"
                  >
                    <div class="flex items-center gap-3">
                      <div class="p-2 rounded-lg bg-muted">
                        {#if source.mode === MigrationMode.MongoDb}
                          <Database class="h-5 w-5" />
                        {:else}
                          <Globe class="h-5 w-5" />
                        {/if}
                      </div>
                      <div>
                        <div class="font-medium">
                          {source.nightscoutUrl ||
                            source.mongoDatabaseName ||
                            "Unknown"}
                        </div>
                        <div class="text-sm text-muted-foreground">
                          Last migration: {formatDate(source.lastMigrationAt)}
                        </div>
                      </div>
                    </div>
                  </div>
                {/each}
              </div>
            </CardContent>
          </Card>
        {/if}
      </Tabs.Content>
    </Tabs.Root>
  {/if}
</div>
