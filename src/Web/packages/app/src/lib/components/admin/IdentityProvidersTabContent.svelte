<script lang="ts">
  import * as Tabs from "$lib/components/ui/tabs";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Alert from "$lib/components/ui/alert";
  import {
    Plus,
    Loader2,
    AlertTriangle,
    Shield,
    ToggleRight,
    ToggleLeft,
    Pencil,
    Trash2,
  } from "lucide-svelte";
  import ProviderIcon from "$lib/components/auth/ProviderIcon.svelte";
  import type { OidcProviderResponse } from "$api";

  interface Props {
    oidcLoading: boolean;
    oidcError: string | null;
    oidcProviders: OidcProviderResponse[];
    openCreateProviderDialog: () => void;
    openEditProviderDialog: (provider: OidcProviderResponse) => void;
    toggleProvider: (provider: OidcProviderResponse) => void;
    deleteProvider: (provider: OidcProviderResponse) => void;
  }

  let {
    oidcLoading,
    oidcError,
    oidcProviders,
    openCreateProviderDialog,
    openEditProviderDialog,
    toggleProvider,
    deleteProvider,
  }: Props = $props();
</script>

<Tabs.Content value="identity-providers">
  <Card>
    <CardHeader class="flex flex-row items-center justify-between">
      <div>
        <CardTitle>Identity Providers</CardTitle>
        <CardDescription>
          Configure OpenID Connect providers for single sign-on.
        </CardDescription>
      </div>
      <Button onclick={openCreateProviderDialog}>
        <Plus class="h-4 w-4" />
        Add Provider
      </Button>
    </CardHeader>
    <CardContent>
      {#if oidcLoading}
        <div class="flex items-center justify-center py-8">
          <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
        </div>
      {:else if oidcError}
        <Alert.Root variant="destructive">
          <AlertTriangle class="h-4 w-4" />
          <Alert.Description>{oidcError}</Alert.Description>
        </Alert.Root>
      {:else if oidcProviders.length === 0}
        <div class="text-center py-8 text-muted-foreground">
          <Shield class="h-12 w-12 mx-auto mb-2 opacity-50" />
          <p>No identity providers configured.</p>
        </div>
      {:else}
        <div class="space-y-2">
          {#each oidcProviders as provider (provider.id)}
            <div class="flex items-center justify-between gap-3 rounded-md border p-3">
              <div class="flex items-center gap-3 min-w-0">
                <ProviderIcon slug={provider.icon} />
                <div class="min-w-0">
                  <div class="flex items-center gap-2">
                    <span class="font-medium truncate">{provider.name}</span>
                    {#if provider.isEnabled}
                      <Badge variant="secondary">Enabled</Badge>
                    {:else}
                      <Badge variant="outline">Disabled</Badge>
                    {/if}
                  </div>
                  <div class="text-xs text-muted-foreground truncate">
                    {provider.issuerUrl}
                  </div>
                </div>
              </div>
              <div class="flex items-center gap-1 shrink-0">
                <Button
                  variant="ghost"
                  size="sm"
                  onclick={() => toggleProvider(provider)}
                  title={provider.isEnabled ? "Disable" : "Enable"}
                >
                  {#if provider.isEnabled}
                    <ToggleRight class="h-4 w-4" />
                  {:else}
                    <ToggleLeft class="h-4 w-4" />
                  {/if}
                </Button>
                <Button
                  variant="ghost"
                  size="sm"
                  onclick={() => openEditProviderDialog(provider)}
                  title="Edit"
                >
                  <Pencil class="h-4 w-4" />
                </Button>
                <Button
                  variant="ghost"
                  size="sm"
                  onclick={() => deleteProvider(provider)}
                  title="Delete"
                >
                  <Trash2 class="h-4 w-4" />
                </Button>
              </div>
            </div>
          {/each}
        </div>
      {/if}
    </CardContent>
  </Card>
</Tabs.Content>