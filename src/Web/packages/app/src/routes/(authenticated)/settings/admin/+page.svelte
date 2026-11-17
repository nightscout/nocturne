<script lang="ts">
  import { onMount, untrack } from "svelte";
  import { page } from "$app/state";
  import {
    Card,
    CardContent,
  } from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import Shield from "@lucide/svelte/icons/shield";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import Bot from "@lucide/svelte/icons/bot";
  import ListChecks from "@lucide/svelte/icons/list-checks";
  import * as rolesRemote from "#lib/api/generated/roles.generated.remote.js";
  import * as oidcRemote from "#lib/api/generated/oidcProviderAdmins.generated.remote.js";
  import * as platformSettingsRemote from "#lib/api/generated/platformSettings.generated.remote.js";
  import * as jobLedgerRemote from "#lib/api/generated/jobLedgers.generated.remote.js";
  import IntegrationsTabContent from "#lib/components/admin/IntegrationsTabContent.svelte";
  import OidcProvidersTabContent from "#lib/components/admin/OidcProvidersTabContent.svelte";
  import JobsTabContent from "#lib/components/admin/JobsTabContent.svelte";
  import OidcProviderDialog from "#lib/components/admin/OidcProviderDialog.svelte";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { withLockoutOverride } from "#lib/components/admin/lockout-override.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import type {
    TenantRoleDto,
    OidcProviderResponse,
    PlatformSettingsSummary,
    CreateOidcProviderRequest,
    JobLedgerListing,
  } from "#lib/api/index.js";
  import { fresh } from "#lib/api/fresh-query.js";

  // State
  const requestedTab = page.url.searchParams.get("tab");
  let activeTab = $state(
    requestedTab === "integrations" || requestedTab === "jobs" ? requestedTab : "identity-providers"
  );
  let loading = $state(true);
  let error = $state<string | null>(null);

  let roles = $state<TenantRoleDto[]>([]);
  let platformSettings = $state<PlatformSettingsSummary[]>([]);

  // ============================================================================
  // Identity Providers (OIDC) state
  // ============================================================================
  let oidcProviders = $state<OidcProviderResponse[]>([]);
  let oidcConfigManaged = $state(false);
  let oidcLoading = $state(false);
  let oidcError = $state<string | null>(null);

  // Provider dialog
  let isProviderDialogOpen = $state(false);
  let editingProvider = $state<OidcProviderResponse | null>(null);

  function openCreateProviderDialog() {
    editingProvider = null;
    isProviderDialogOpen = true;
  }

  function openEditProviderDialog(p: OidcProviderResponse) {
    editingProvider = p;
    isProviderDialogOpen = true;
  }

  async function loadOidcData() {
    oidcLoading = true;
    oidcError = null;
    try {
      const [managed, providers] = await Promise.all([
        fresh(oidcRemote.getConfigManaged()),
        fresh(oidcRemote.getAll()),
      ]);
      oidcConfigManaged = managed?.isConfigManaged ?? false;
      oidcProviders = providers ?? [];
      // The Identity Providers tab is hidden when config-managed; fall back to Integrations.
      if (oidcConfigManaged && activeTab === "identity-providers") {
        activeTab = "integrations";
      }
    } catch (err) {
      console.error("Failed to load OIDC providers:", err);
      oidcError = remoteErrorMessage(err, "Failed to load identity providers");
    } finally {
      oidcLoading = false;
    }
  }

  async function saveProvider(providerData: CreateOidcProviderRequest) {
    try {
      const id = editingProvider?.id;
      if (id) {
        // The generated update takes no force flag, so an override switches the provider off
        // through disable first; the update that follows then no longer turns it off.
        const saved = await withLockoutOverride(editingProvider?.name, async (force) => {
          if (force) await oidcRemote.disable({ id, force });
          await oidcRemote.update({ id, request: providerData });
        });
        if (!saved) return;
      } else {
        await oidcRemote.create(providerData);
      }
      isProviderDialogOpen = false;
      await loadOidcData();
    } catch (err: unknown) {
      console.error("Failed to save provider:", err);
      throw err;
    }
  }

  async function deleteProvider(p: OidcProviderResponse) {
    const id = p.id;
    if (!id) return;
    if (!confirm(`Delete provider "${p.name}"?`)) return;
    try {
      await withLockoutOverride(p.name, (force) => oidcRemote.remove({ id, force }));
      await loadOidcData();
    } catch (err: unknown) {
      oidcError = describeSubmitError(err, "Failed to delete provider.");
    }
  }

  async function toggleProvider(p: OidcProviderResponse) {
    const id = p.id;
    if (!id) return;
    try {
      if (p.isEnabled) {
        await withLockoutOverride(p.name, (force) => oidcRemote.disable({ id, force }));
      } else {
        await oidcRemote.enable(id);
      }
      await loadOidcData();
    } catch (err: unknown) {
      oidcError = describeSubmitError(
        err,
        "Failed to change whether this provider is enabled."
      );
    }
  }

  let jobs = $state<JobLedgerListing | null>(null);
  let jobsLoading = $state(false);
  let jobsError = $state<string | null>(null);

  async function loadJobs() {
    jobsLoading = true;
    jobsError = null;
    try {
      jobs = await fresh(jobLedgerRemote.list());
    } catch (err) {
      console.error("Failed to load jobs:", err);
      jobsError = remoteErrorMessage(err, "Failed to load the background jobs.");
    } finally {
      jobsLoading = false;
    }
  }

  // Load data
  async function loadData() {
    loading = true;
    error = null;
    try {
      const [rols, platformSettingsList] = await Promise.all([
        fresh(rolesRemote.getRoles()),
        fresh(platformSettingsRemote.getAll()),
      ]);
      await loadOidcData();
      roles = rols || [];
      platformSettings = platformSettingsList ?? [];
    } catch (err) {
      console.error("Failed to load admin data:", err);
      error = remoteErrorMessage(err, "Failed to load admin data");
    } finally {
      loading = false;
    }
  }

  onMount(() => {
    void loadData();
  });

  // The ledger is read only once its tab is opened.
  $effect(() => {
    if (activeTab === "jobs") untrack(() => {
      if (jobs === null && !jobsLoading) void loadJobs();
    });
  });

  // ============================================================================
  // Platform settings handlers
  // ============================================================================

  async function handlePlatformSettingsSave(category: string, enabled: boolean, fields: Record<string, string>) {
    await platformSettingsRemote.upsert({ category, request: { enabled, fields } });
    const updated = await fresh(platformSettingsRemote.getAll());
    if (updated) platformSettings = updated;
  }

  async function handlePlatformSettingsDelete(category: string) {
    await platformSettingsRemote.remove(category);
    const updated = await fresh(platformSettingsRemote.getAll());
    if (updated) platformSettings = updated;
  }
</script>

<svelte:head>
  <title>Administration - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto p-3 @md:p-6 max-w-5xl">
  <!-- Header -->
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
      <Shield class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Administration</h1>
      <p class="text-muted-foreground">
        Manage identity providers, integrations, and background jobs
      </p>
    </div>
  </div>

  {#if loading}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else if error}
    <Card variant="destructive">
      <CardContent class="py-6 text-center">
        <AlertTriangle class="h-8 w-8 text-destructive mx-auto mb-2" />
        <p class="text-destructive">{error}</p>
        <Button variant="outline" class="mt-4" onclick={loadData}>Retry</Button>
      </CardContent>
    </Card>
  {:else}
    <Tabs.Root bind:value={activeTab} class="space-y-6">
      <Tabs.List variant="wrap">
        {#if !oidcConfigManaged}
          <Tabs.Trigger value="identity-providers">
            <Shield class="h-4 w-4" />
            Identity providers
            {#if oidcProviders.length > 0}
              <Badge variant="secondary" class="ml-1">{oidcProviders.length}</Badge>
            {/if}
          </Tabs.Trigger>
        {/if}
        <Tabs.Trigger value="integrations">
          <Bot class="h-4 w-4" />
          Integrations
        </Tabs.Trigger>
        <Tabs.Trigger value="jobs">
          <ListChecks class="h-4 w-4" />
          Jobs
        </Tabs.Trigger>
      </Tabs.List>

      <OidcProvidersTabContent
        providers={oidcProviders}
        configManaged={oidcConfigManaged}
        loading={oidcLoading}
        error={oidcError}
        onAdd={openCreateProviderDialog}
        onEdit={openEditProviderDialog}
        onDelete={deleteProvider}
        onToggle={toggleProvider}
      />

      <IntegrationsTabContent
        platforms={platformSettings}
        onSave={handlePlatformSettingsSave}
        onDelete={handlePlatformSettingsDelete}
      />

      <JobsTabContent
        jobs={jobs?.rows ?? []}
        capped={jobs?.capped ?? false}
        loading={jobsLoading}
        error={jobsError}
        onRefresh={loadJobs}
      />
    </Tabs.Root>
  {/if}

  <!-- OIDC Provider Create/Edit Dialog -->
  <OidcProviderDialog
    bind:open={isProviderDialogOpen}
    bind:editingProvider
    {roles}
    onSave={saveProvider}
    onCancel={() => {}}
  />
</div>
