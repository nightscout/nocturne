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
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import * as Alert from "$lib/components/ui/alert";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import {
    RefreshCw,
    Loader2,
    AlertTriangle,
    CheckCircle2,
    XCircle,
    Plug,
    Building2,
  } from "lucide-svelte";
  import * as tenantRemote from "$api/generated/tenants.generated.remote";
  import {
    getTenantConnectors,
    resetTenantCursors,
    type TenantConnectorsDto,
    type TenantCursorResetResult,
  } from "../connector-cursors.remote";
  import type { TenantDto } from "$api";

  // Tenant list (platform admin)
  let tenants = $state<TenantDto[]>([]);
  let tenantsLoading = $state(true);
  let tenantsError = $state<string | null>(null);

  // Selected tenant + its connectors
  let selectedTenantId = $state<string | undefined>(undefined);
  let connectors = $state<TenantConnectorsDto | null>(null);
  let connectorsLoading = $state(false);
  let connectorsError = $state<string | null>(null);

  // Reset options
  let fromDate = $state<string>(""); // yyyy-MM-dd (optional lower bound)

  // Reset execution
  let confirmOpen = $state(false);
  let resetting = $state(false);
  let resetResult = $state<TenantCursorResetResult | null>(null);
  let resetError = $state<string | null>(null);

  const selectedTenant = $derived(
    tenants.find((t) => t.id === selectedTenantId),
  );

  async function loadTenants() {
    tenantsLoading = true;
    tenantsError = null;
    try {
      tenants = (await tenantRemote.getAll()) ?? [];
    } catch (err) {
      console.error("Failed to load tenants:", err);
      tenantsError = "Failed to load tenants.";
    } finally {
      tenantsLoading = false;
    }
  }

  $effect(() => {
    loadTenants();
  });

  async function onTenantChange(value: string | undefined) {
    selectedTenantId = value;
    connectors = null;
    resetResult = null;
    resetError = null;
    if (!value) return;

    connectorsLoading = true;
    connectorsError = null;
    try {
      connectors = await getTenantConnectors(value);
    } catch (err) {
      console.error("Failed to load connectors:", err);
      connectorsError = "Failed to load this tenant's connectors.";
    } finally {
      connectorsLoading = false;
    }
  }

  async function runReset() {
    if (!selectedTenantId) return;
    resetting = true;
    resetError = null;
    resetResult = null;
    try {
      // Convert the optional date-only lower bound to an ISO instant.
      const fromIso = fromDate ? new Date(`${fromDate}T00:00:00Z`).toISOString() : null;
      resetResult = await resetTenantCursors({
        tenantId: selectedTenantId,
        from: fromIso,
        dataTypes: null, // first cut resets every supported type
      });
      // Refresh the connector list so the admin sees updated sync timestamps.
      connectors = await getTenantConnectors(selectedTenantId);
    } catch (err) {
      console.error("Cursor reset failed:", err);
      resetError = "Cursor reset failed. Check the server logs for details.";
    } finally {
      resetting = false;
      confirmOpen = false;
    }
  }

  function formatTimestamp(value: string | null | undefined): string {
    if (!value) return "Never";
    return new Date(value).toLocaleString(undefined, {
      year: "numeric",
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  const hasConnectors = $derived((connectors?.connectors.length ?? 0) > 0);
</script>

<svelte:head>
  <title>Reset Connector Cursors - Administration - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl space-y-6 p-3 @md:p-6">
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
      <RefreshCw class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Reset Connector Cursors</h1>
      <p class="text-muted-foreground">
        Re-pull a tenant's connector history after fixing a connector bug
      </p>
    </div>
  </div>

  <Alert.Root>
    <AlertTriangle class="h-4 w-4" />
    <Alert.Title>Heads up</Alert.Title>
    <Alert.Description>
      This forces a full re-pull of history for every configured connector on
      the selected tenant. Re-ingested records dedupe on their idempotency keys,
      so it is safe to re-run, but a large re-pull can take several minutes and
      runs synchronously.
    </Alert.Description>
  </Alert.Root>

  <!-- Tenant picker -->
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Building2 class="h-5 w-5" />
        Target tenant
      </CardTitle>
      <CardDescription>Pick the tenant whose connectors you want to reset</CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      {#if tenantsLoading}
        <div class="flex items-center gap-2 text-muted-foreground">
          <Loader2 class="h-4 w-4 animate-spin" />
          Loading tenants...
        </div>
      {:else if tenantsError}
        <Alert.Root variant="destructive">
          <AlertTriangle class="h-4 w-4" />
          <Alert.Description>{tenantsError}</Alert.Description>
        </Alert.Root>
      {:else}
        <div class="space-y-2">
          <Label for="tenant-select">Tenant</Label>
          <Select.Root
            type="single"
            value={selectedTenantId}
            onValueChange={onTenantChange}
          >
            <Select.Trigger id="tenant-select" class="w-full">
              {selectedTenant
                ? `${selectedTenant.displayName} (${selectedTenant.slug})`
                : "Select a tenant"}
            </Select.Trigger>
            <Select.Content>
              {#each tenants as t (t.id)}
                <Select.Item value={t.id ?? ""} label={`${t.displayName} (${t.slug})`} />
              {/each}
            </Select.Content>
          </Select.Root>
        </div>

        <div class="space-y-2">
          <Label for="from-date">Lower bound (optional)</Label>
          <Input id="from-date" type="date" bind:value={fromDate} class="w-full" />
          <p class="text-xs text-muted-foreground">
            Leave empty to re-pull all available history.
          </p>
        </div>
      {/if}
    </CardContent>
  </Card>

  <!-- Connectors + current sync status -->
  {#if selectedTenantId}
    <Card>
      <CardHeader class="flex flex-row items-center justify-between">
        <div>
          <CardTitle class="flex items-center gap-2">
            <Plug class="h-5 w-5" />
            Connectors
          </CardTitle>
          <CardDescription>Current sync status for {selectedTenant?.slug}</CardDescription>
        </div>
        <Button
          onclick={() => (confirmOpen = true)}
          disabled={!hasConnectors || connectorsLoading || resetting}
        >
          {#if resetting}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
          {:else}
            <RefreshCw class="mr-2 h-4 w-4" />
          {/if}
          Reset cursors
        </Button>
      </CardHeader>
      <CardContent class="space-y-3">
        {#if connectorsLoading}
          <div class="flex items-center gap-2 text-muted-foreground">
            <Loader2 class="h-4 w-4 animate-spin" />
            Loading connectors...
          </div>
        {:else if connectorsError}
          <Alert.Root variant="destructive">
            <AlertTriangle class="h-4 w-4" />
            <Alert.Description>{connectorsError}</Alert.Description>
          </Alert.Root>
        {:else if !hasConnectors}
          <p class="text-sm text-muted-foreground">
            This tenant has no configured connectors.
          </p>
        {:else}
          {#each connectors!.connectors as c (c.connectorName)}
            {@const resultForConnector = resetResult?.connectors.find(
              (r) => r.connectorName === c.connectorName,
            )}
            <div class="rounded-lg border p-3">
              <div class="flex items-center justify-between">
                <div class="flex items-center gap-2 font-medium">
                  {c.connectorName}
                  {#if c.isHealthy}
                    <Badge variant="secondary">Healthy</Badge>
                  {:else}
                    <Badge variant="destructive">Unhealthy</Badge>
                  {/if}
                </div>
                {#if resultForConnector}
                  {#if resultForConnector.result.success}
                    <span class="flex items-center gap-1 text-sm text-green-600">
                      <CheckCircle2 class="h-4 w-4" /> Reset
                    </span>
                  {:else}
                    <span class="flex items-center gap-1 text-sm text-destructive">
                      <XCircle class="h-4 w-4" /> Failed
                    </span>
                  {/if}
                {/if}
              </div>
              <div class="mt-2 grid grid-cols-2 gap-2 text-xs text-muted-foreground">
                <div>
                  <span class="font-medium">Last successful sync:</span>
                  {formatTimestamp(c.lastSuccessfulSync)}
                </div>
                <div>
                  <span class="font-medium">Last attempt:</span>
                  {formatTimestamp(c.lastSyncAttempt)}
                </div>
              </div>
              {#if c.lastErrorMessage}
                <p class="mt-1 text-xs text-destructive">{c.lastErrorMessage}</p>
              {/if}
              {#if resultForConnector}
                <p class="mt-1 text-xs">{resultForConnector.result.message}</p>
              {/if}
            </div>
          {/each}
        {/if}

        {#if resetError}
          <Alert.Root variant="destructive">
            <AlertTriangle class="h-4 w-4" />
            <Alert.Description>{resetError}</Alert.Description>
          </Alert.Root>
        {/if}
      </CardContent>
    </Card>
  {/if}
</div>

<!-- Confirmation -->
<AlertDialog.Root bind:open={confirmOpen}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Reset cursors for {selectedTenant?.slug}?</AlertDialog.Title>
      <AlertDialog.Description>
        This re-pulls history for all {connectors?.connectors.length ?? 0}
        connector(s) configured on this tenant
        {#if fromDate}
          from {fromDate}
        {:else}
          from the beginning
        {/if}. The request runs synchronously and may take a while.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel disabled={resetting}>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action onclick={runReset} disabled={resetting}>
        {#if resetting}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {/if}
        Reset cursors
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
