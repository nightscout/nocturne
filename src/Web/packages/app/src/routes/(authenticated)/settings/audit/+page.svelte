<script lang="ts">
  import { page } from "$app/state";
  import * as Card from "$lib/components/ui/card";
  import * as Tabs from "$lib/components/ui/tabs";
  import { Switch } from "$lib/components/ui/switch";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import {
    ScrollText,
    Settings2,
    Loader2,
    Info,
    X,
  } from "lucide-svelte";
  import {
    getMutationAuditLog,
    getReadAccessAuditLog,
    getAuditConfig,
    updateAuditConfig,
  } from "$lib/api/generated/audits.generated.remote";
  import AuditMutationsTable from "$lib/components/audit/AuditMutationsTable.svelte";
  import AuditReadsTable from "$lib/components/audit/AuditReadsTable.svelte";

  // Permissions
  const effectivePermissions: string[] = $derived(
    (page.data as any).effectivePermissions ?? [],
  );
  const canManageAudit = $derived(
    effectivePermissions.includes("audit.manage") ||
      effectivePermissions.includes("*"),
  );

  // --- Config ---
  const configQuery = $derived(getAuditConfig());
  const config = $derived(configQuery.current);

  let readAuditEnabled = $state(false);
  let readRetentionDays = $state("");
  let mutationRetentionDays = $state("");
  let isSaving = $state(false);
  let configLoaded = $state(false);

  $effect(() => {
    if (config && !configLoaded) {
      readAuditEnabled = config.readAuditEnabled ?? false;
      readRetentionDays = config.readAuditRetentionDays?.toString() ?? "";
      mutationRetentionDays = config.mutationAuditRetentionDays?.toString() ?? "";
      configLoaded = true;
    }
  });

  async function saveConfig() {
    isSaving = true;
    try {
      await updateAuditConfig({
        readAuditEnabled,
        readAuditRetentionDays: readRetentionDays ? parseInt(readRetentionDays) : null,
        mutationAuditRetentionDays: mutationRetentionDays ? parseInt(mutationRetentionDays) : null,
      });
      configLoaded = false;
    } finally {
      isSaving = false;
    }
  }

  async function enableReadAudit() {
    isSaving = true;
    try {
      await updateAuditConfig({
        readAuditEnabled: true,
        readAuditRetentionDays: config?.readAuditRetentionDays ?? null,
        mutationAuditRetentionDays: config?.mutationAuditRetentionDays ?? null,
      });
      readAuditEnabled = true;
      configLoaded = false;
    } finally {
      isSaving = false;
    }
  }

  // --- Tab state ---
  let activeTab = $state("mutations");

  // --- Mutation log server-side filters ---
  let mFrom = $state(
    new Date(Date.now() - 7 * 24 * 60 * 60 * 1000).toISOString().split("T")[0],
  );
  let mTo = $state(new Date().toISOString().split("T")[0]);
  let mGlobalFilter = $state("");

  const mutationsQuery = $derived(
    getMutationAuditLog({
      from: new Date(mFrom),
      to: new Date(mTo + "T23:59:59"),
      limit: 500,
      offset: 0,
      sort: "created_at_desc",
    }),
  );
  const mutationsResult = $derived(mutationsQuery.current);
  const mutations = $derived((mutationsResult as any)?.data ?? []);
  const mutationsTotal = $derived((mutationsResult as any)?.pagination?.total ?? 0);

  // --- Read access log server-side filters ---
  let rFrom = $state(
    new Date(Date.now() - 7 * 24 * 60 * 60 * 1000).toISOString().split("T")[0],
  );
  let rTo = $state(new Date().toISOString().split("T")[0]);
  let rGlobalFilter = $state("");

  const readsQuery = $derived(
    getReadAccessAuditLog({
      from: new Date(rFrom),
      to: new Date(rTo + "T23:59:59"),
      limit: 500,
      offset: 0,
      sort: "created_at_desc",
    }),
  );
  const readsResult = $derived(readsQuery.current);
  const reads = $derived((readsResult as any)?.data ?? []);
  const readsTotal = $derived((readsResult as any)?.pagination?.total ?? 0);

  // --- Filter state helpers ---
  const mHasDateFilter = $derived(
    mFrom !== new Date(Date.now() - 7 * 24 * 60 * 60 * 1000).toISOString().split("T")[0] ||
    mTo !== new Date().toISOString().split("T")[0]
  );

  const rHasDateFilter = $derived(
    rFrom !== new Date(Date.now() - 7 * 24 * 60 * 60 * 1000).toISOString().split("T")[0] ||
    rTo !== new Date().toISOString().split("T")[0]
  );

  function resetMutationDateFilter() {
    mFrom = new Date(Date.now() - 7 * 24 * 60 * 60 * 1000).toISOString().split("T")[0];
    mTo = new Date().toISOString().split("T")[0];
  }

  function resetReadDateFilter() {
    rFrom = new Date(Date.now() - 7 * 24 * 60 * 60 * 1000).toISOString().split("T")[0];
    rTo = new Date().toISOString().split("T")[0];
  }
</script>

<svelte:head>
  <title>Audit Log - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-4xl p-6 space-y-6">
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
      <ScrollText class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Audit Log</h1>
      <p class="text-muted-foreground">
        View data changes and access history for compliance
      </p>
    </div>
  </div>

  <!-- Config Card (audit.manage only) -->
  {#if canManageAudit}
    <Card.Root>
      <Card.Header>
        <Card.Title class="flex items-center gap-2">
          <Settings2 class="h-4 w-4" />
          Audit Configuration
        </Card.Title>
      </Card.Header>
      <Card.Content>
        <div class="grid gap-4 sm:grid-cols-3">
          <div class="flex items-center gap-3">
            <Switch
              checked={readAuditEnabled}
              onCheckedChange={(v) => (readAuditEnabled = v === true)}
            />
            <Label>Read Access Logging</Label>
          </div>
          <div class="space-y-1">
            <Label for="read-retention">Read Log Retention (days)</Label>
            <Input
              id="read-retention"
              type="number"
              min="1"
              placeholder="Unlimited"
              bind:value={readRetentionDays}
            />
          </div>
          <div class="space-y-1">
            <Label for="mutation-retention">Mutation Log Retention (days)</Label>
            <Input
              id="mutation-retention"
              type="number"
              min="1"
              placeholder="Unlimited"
              bind:value={mutationRetentionDays}
            />
          </div>
        </div>
      </Card.Content>
      <Card.Footer>
        <Button onclick={saveConfig} disabled={isSaving}>
          {#if isSaving}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
          {/if}
          Save Configuration
        </Button>
      </Card.Footer>
    </Card.Root>
  {/if}

  <!-- Tabbed Log Viewer -->
  <Tabs.Root bind:value={activeTab} class="space-y-4">
    <Tabs.List class="grid w-full grid-cols-2">
      <Tabs.Trigger value="mutations">
        Data Changes
        {#if mutationsTotal > 0}
          <Badge variant="secondary" class="ml-2">{mutationsTotal}</Badge>
        {/if}
      </Tabs.Trigger>
      <Tabs.Trigger value="reads">
        Data Access
        {#if readsTotal > 0}
          <Badge variant="secondary" class="ml-2">{readsTotal}</Badge>
        {/if}
      </Tabs.Trigger>
    </Tabs.List>

    <!-- === Mutation Audit Log Tab === -->
    <Tabs.Content value="mutations" class="space-y-4">
      <!-- Date Range Filter Card -->
      <Card.Root>
        <Card.Content class="p-4">
          <div class="flex flex-col gap-4 md:flex-row md:items-end md:justify-between">
            <div class="flex flex-1 flex-col gap-4 md:flex-row md:items-end">
              <div class="space-y-1">
                <Label for="m-from">From</Label>
                <Input id="m-from" type="date" bind:value={mFrom} />
              </div>
              <div class="space-y-1">
                <Label for="m-to">To</Label>
                <Input id="m-to" type="date" bind:value={mTo} />
              </div>
            </div>

            <div class="flex items-center gap-2">
              {#if mHasDateFilter}
                <Button variant="ghost" size="sm" onclick={resetMutationDateFilter}>
                  <X class="mr-1 h-4 w-4" />
                  Reset dates
                </Button>
              {/if}
            </div>
          </div>

          {#if mHasDateFilter}
            <div class="mt-4 flex flex-wrap items-center gap-2 pt-4 border-t text-sm">
              <span class="text-muted-foreground">Date range:</span>
              <Badge variant="outline" class="gap-1">
                {mFrom} to {mTo}
                <button
                  onclick={resetMutationDateFilter}
                  class="ml-1 hover:text-foreground"
                >
                  <X class="h-3 w-3" />
                </button>
              </Badge>
            </div>
          {/if}
        </Card.Content>
      </Card.Root>

      <!-- Mutations Smart Table -->
      <Card.Root>
        <Card.Content class="p-0">
          <AuditMutationsTable
            rows={mutations}
            bind:globalFilter={mGlobalFilter}
          />
        </Card.Content>
      </Card.Root>
    </Tabs.Content>

    <!-- === Read Access Log Tab === -->
    <Tabs.Content value="reads" class="space-y-4">
      {#if !config?.readAuditEnabled && !readAuditEnabled}
        <!-- Empty state: read audit not enabled -->
        <Card.Root>
          <Card.Content class="flex flex-col items-center justify-center py-12 text-center">
            <Info class="h-10 w-10 text-muted-foreground mb-4" />
            <p class="text-lg font-medium mb-2">Read audit is not enabled</p>
            {#if canManageAudit}
              <p class="text-sm text-muted-foreground mb-4">
                Enable read access logging to track who views patient data.
              </p>
              <Button onclick={enableReadAudit} disabled={isSaving}>
                {#if isSaving}
                  <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                {/if}
                Enable now
              </Button>
            {:else}
              <p class="text-sm text-muted-foreground">
                Contact your admin to enable read audit logging.
              </p>
            {/if}
          </Card.Content>
        </Card.Root>
      {:else}
        <!-- Date Range Filter Card -->
        <Card.Root>
          <Card.Content class="p-4">
            <div class="flex flex-col gap-4 md:flex-row md:items-end md:justify-between">
              <div class="flex flex-1 flex-col gap-4 md:flex-row md:items-end">
                <div class="space-y-1">
                  <Label for="r-from">From</Label>
                  <Input id="r-from" type="date" bind:value={rFrom} />
                </div>
                <div class="space-y-1">
                  <Label for="r-to">To</Label>
                  <Input id="r-to" type="date" bind:value={rTo} />
                </div>
              </div>

              <div class="flex items-center gap-2">
                {#if rHasDateFilter}
                  <Button variant="ghost" size="sm" onclick={resetReadDateFilter}>
                    <X class="mr-1 h-4 w-4" />
                    Reset dates
                  </Button>
                {/if}
              </div>
            </div>

            {#if rHasDateFilter}
              <div class="mt-4 flex flex-wrap items-center gap-2 pt-4 border-t text-sm">
                <span class="text-muted-foreground">Date range:</span>
                <Badge variant="outline" class="gap-1">
                  {rFrom} to {rTo}
                  <button
                    onclick={resetReadDateFilter}
                    class="ml-1 hover:text-foreground"
                  >
                    <X class="h-3 w-3" />
                  </button>
                </Badge>
              </div>
            {/if}
          </Card.Content>
        </Card.Root>

        <!-- Reads Smart Table -->
        <Card.Root>
          <Card.Content class="p-0">
            <AuditReadsTable
              rows={reads}
              bind:globalFilter={rGlobalFilter}
            />
          </Card.Content>
        </Card.Root>
      {/if}
    </Tabs.Content>
  </Tabs.Root>
</div>
