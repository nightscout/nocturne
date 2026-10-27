<script lang="ts">
  import { page } from "$app/state";
  import { invalidate } from "$app/navigation";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import Globe from "@lucide/svelte/icons/globe";
  import Lock from "@lucide/svelte/icons/lock";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import {
    getTenantSettings,
    setDisplayName,
  } from "#lib/api/generated/tenantSettings.generated.remote.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import { formatNumericDate } from "#lib/utils/formatting.js";
  import { SHELL_DATA_KEY } from "#lib/constants/shell-data.js";
  import ApiReference from "#lib/components/settings/ApiReference.svelte";

  const MAX_NAME_LENGTH = 256;

  const canManageSettings = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "tenant.settings"),
  );

  const settingsQuery = $derived(canManageSettings ? getTenantSettings() : null);
  retainQuery(() => settingsQuery);
  const settings = $derived(settingsQuery?.current);

  // Null until the user types, so the field follows the server value.
  let draft = $state<string | null>(null);
  let saving = $state(false);
  let saved = $state(false);
  let saveError = $state<string | null>(null);

  const name = $derived(draft ?? settings?.displayName ?? "");
  const trimmed = $derived(name.trim());
  const dirty = $derived(
    draft !== null && trimmed !== (settings?.displayName ?? ""),
  );
  // The demo's account holds tenant.settings, but the endpoint refuses it (DenyDemoSubject).
  const canRename = $derived(page.data.refusedAsDemoSubject !== true);
  const canSave = $derived(
    canRename &&
    dirty && trimmed.length > 0 && trimmed.length <= MAX_NAME_LENGTH && !saving,
  );

  async function save() {
    if (!canSave) return;
    saving = true;
    saved = false;
    saveError = null;
    try {
      await setDisplayName({ displayName: trimmed });
      draft = null;
      saved = true;
      void invalidate(SHELL_DATA_KEY);
    } catch (err) {
      saveError = describeSubmitError(
        err,
        "Could not save the site name. Try again.",
      );
    } finally {
      saving = false;
    }
  }
</script>

<svelte:head>
  <title>Site details - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
  <div class="flex items-center gap-3">
    <div
      class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10"
    >
      <Globe class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Site details</h1>
      <p class="text-muted-foreground">
        The name and address of this Nocturne site
      </p>
    </div>
  </div>

  {#if !canManageSettings}
    <Alert.Root>
      <Lock class="h-4 w-4" />
      <Alert.Title>You cannot see the site details</Alert.Title>
      <Alert.Description>
        Only a site owner or administrator can see and change the site details.
      </Alert.Description>
    </Alert.Root>
  {:else if settingsQuery?.error}
    <Alert.Root variant="destructive">
      <AlertTriangle class="h-4 w-4" />
      <Alert.Title>Could not load the site details</Alert.Title>
      <Alert.Description>
        {remoteErrorMessage(settingsQuery.error, "Reload the page to try again.")}
      </Alert.Description>
    </Alert.Root>
  {:else if !settings}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else}
    <Card.Root>
      <Card.Header>
        <Card.Title>Site name</Card.Title>
        <Card.Description>
          The name that Nocturne shows for this site.
        </Card.Description>
      </Card.Header>
      <Card.Content>
        <form
          class="flex flex-col gap-3 @md:flex-row @md:items-end"
          onsubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <div class="flex-1 space-y-2">
            <Label for="site-display-name">Site name</Label>
            <Input
              id="site-display-name"
              value={name}
              maxlength={MAX_NAME_LENGTH}
              disabled={!canRename}
              aria-invalid={draft !== null && trimmed.length === 0}
              oninput={(e) => {
                draft = e.currentTarget.value;
                saved = false;
              }}
            />
          </div>
          <Button type="submit" disabled={!canSave}>
            {#if saving}
              <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            {/if}
            Save
          </Button>
        </form>
        {#if !canRename}
          <p class="mt-2 text-sm text-muted-foreground">
            You cannot change the name of the demo site.
          </p>
        {/if}
        {#if draft !== null && trimmed.length === 0}
          <p class="mt-2 text-sm text-destructive">Enter a site name.</p>
        {/if}
        {#if saveError}
          <p class="mt-2 text-sm text-destructive" role="alert">{saveError}</p>
        {:else if saved}
          <p class="mt-2 text-sm text-muted-foreground" role="status">
            Site name saved.
          </p>
        {/if}
      </Card.Content>
    </Card.Root>

    <Card.Root>
      <Card.Header>
        <Card.Title>Address</Card.Title>
        <Card.Description>
          You cannot change the address of a site.
        </Card.Description>
      </Card.Header>
      <Card.Content>
        <dl class="grid grid-cols-1 gap-4 @md:grid-cols-2">
          <div>
            <dt class="text-sm font-medium text-muted-foreground">Slug</dt>
            <dd class="mt-1 font-mono text-sm">{settings.slug}</dd>
          </div>
          <div>
            <dt class="text-sm font-medium text-muted-foreground">Created</dt>
            <dd class="mt-1 text-sm">
              {settings.sysCreatedAt
                ? formatNumericDate(new Date(settings.sysCreatedAt))
                : "---"}
            </dd>
          </div>
        </dl>
      </Card.Content>
    </Card.Root>
  {/if}

  <ApiReference />
</div>
