<script lang="ts">
  import { onMount } from "svelte";
  import {
    Card,
    CardContent,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Tabs from "$lib/components/ui/tabs";
  import {
    Shield,
    Loader2,
    AlertTriangle,
    Bot,
  } from "lucide-svelte";
  import * as rolesRemote from "$lib/api/generated/roles.generated.remote";
  import * as oidcRemote from "$lib/api/generated/oidcProviderAdmins.generated.remote";
  import * as platformSettingsRemote from "$lib/api/generated/platformSettings.generated.remote";
  import IntegrationsTabContent from "$lib/components/admin/IntegrationsTabContent.svelte";
  import OidcProvidersTabContent from "$lib/components/admin/OidcProvidersTabContent.svelte";
  import OidcProviderDialog from "$lib/components/admin/OidcProviderDialog.svelte";
  import type {
    TenantRoleDto,
    OidcProviderResponse,
    PlatformSettingsSummary,
  } from "$api";

  // State
  let activeTab = $state("identity-providers");
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
        oidcRemote.getConfigManaged().run(),
        oidcRemote.getAll().run(),
      ]);
      oidcConfigManaged = managed?.isConfigManaged ?? false;
      oidcProviders = providers ?? [];
      // The Identity Providers tab is hidden when config-managed; fall back to Integrations.
      if (oidcConfigManaged && activeTab === "identity-providers") {
        activeTab = "integrations";
      }
    } catch (err) {
      console.error("Failed to load OIDC providers:", err);
      const body = (err as { body?: { message?: string; detail?: string } })?.body;
      oidcError =
        body?.message ??
        body?.detail ??
        (err instanceof Error ? err.message : "Failed to load identity providers");
    } finally {
      oidcLoading = false;
    }
  }

  async function saveProvider(providerData: any) {
    try {
      if (editingProvider?.id) {
        await oidcRemote.update({ id: editingProvider.id, request: providerData });
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
    if (!p.id) return;
    if (!confirm(`Delete provider "${p.name}"?`)) return;
    try {
      await oidcRemote.remove(p.id);
      await loadOidcData();
    } catch (err: unknown) {
      const message =
        err instanceof Error ? err.message : "Failed to delete provider";
      oidcError = message.includes("would_lock_out_users")
        ? "Deleting this provider would lock out all users."
        : message;
    }
  }

  async function toggleProvider(p: OidcProviderResponse) {
    if (!p.id) return;
    try {
      if (p.isEnabled) {
        await oidcRemote.disable(p.id);
      } else {
        await oidcRemote.enable(p.id);
      }
      await loadOidcData();
    } catch (err: unknown) {
      const message =
        err instanceof Error ? err.message : "Failed to toggle provider";
      oidcError = message.includes("would_lock_out_users")
        ? "Disabling this provider would lock out all users."
        : message;
    }
  }

  // Load data
  async function loadData() {
    loading = true;
    error = null;
    try {
      const [rols, platformSettingsList] = await Promise.all([
        rolesRemote.getRoles().run(),
        platformSettingsRemote.getAll().run(),
      ]);
      await loadOidcData();
      roles = rols || [];
      platformSettings = platformSettingsList ?? [];
    } catch (err) {
      console.error("Failed to load admin data:", err);
      error = "Failed to load admin data";
    } finally {
      loading = false;
    }
  }

  // Initial load. `.run()` rejects when called during the render/effect flush,
  // so defer the bootstrap to a microtask — onMount's synchronous body still
  // counts as render.
  onMount(() => {
    queueMicrotask(() => {
      loadData();
    });
  });

  // ============================================================================
  // Platform settings handlers
  // ============================================================================

  async function handlePlatformSettingsSave(category: string, enabled: boolean, fields: Record<string, string>) {
    await platformSettingsRemote.upsert({ category, request: { enabled, fields } });
    const updated = await platformSettingsRemote.getAll().run();
    if (updated) platformSettings = updated;
  }

  async function handlePlatformSettingsDelete(category: string) {
    await platformSettingsRemote.remove(category);
    const updated = await platformSettingsRemote.getAll().run();
    if (updated) platformSettings = updated;
  }
</script>

<svelte:head>
  <title>Administration - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto p-3 @md:p-6 max-w-5xl">
  <!-- Header -->
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
      <Shield class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Administration</h1>
      <p class="text-muted-foreground">
        Manage identity providers and integrations
      </p>
    </div>
  </div>

  {#if loading}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else if error}
    <Card class="border-destructive">
      <CardContent class="py-6 text-center">
        <AlertTriangle class="h-8 w-8 text-destructive mx-auto mb-2" />
        <p class="text-destructive">{error}</p>
        <Button variant="outline" class="mt-4" onclick={loadData}>Retry</Button>
      </CardContent>
    </Card>
  {:else}
    <Tabs.Root bind:value={activeTab} class="space-y-6">
      <Tabs.List class={oidcConfigManaged ? "grid w-full grid-cols-1" : "grid w-full grid-cols-2"}>
        {#if !oidcConfigManaged}
          <Tabs.Trigger value="identity-providers" class="gap-2">
            <Shield class="h-4 w-4" />
            Identity Providers
            {#if oidcProviders.length > 0}
              <Badge variant="secondary" class="ml-1">{oidcProviders.length}</Badge>
            {/if}
          </Tabs.Trigger>
        {/if}
        <Tabs.Trigger value="integrations" class="gap-2">
          <Bot class="h-4 w-4" />
          Integrations
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
