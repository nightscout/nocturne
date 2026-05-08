<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Shield,
    Trash2,
    Check,
    AlertTriangle,
    Clock,
    LoaderCircle,
    Plus,
    BadgeCheck,
    ExternalLink,
  } from "lucide-svelte";
  import { formatDate } from "$lib/utils/formatting";
  import { list, revoke } from "$lib/api/generated/connectedApps.generated.remote";
  import { getOAuthScopeDescription } from "$lib/constants/oauth-scopes";

  // Remote queries
  const appsQuery = list();
  const apps = $derived(appsQuery.current ?? []);

  // Loading/error states
  let isRevoking = $state<string | null>(null);
  let errorMessage = $state<string | null>(null);
  let successMessage = $state<string | null>(null);

  /** Clear messages after a delay */
  function clearMessages() {
    setTimeout(() => {
      successMessage = null;
      errorMessage = null;
    }, 3000);
  }

  /** Handle revoking a grant */
  async function handleRevoke(grantId: string) {
    isRevoking = grantId;
    errorMessage = null;
    try {
      await revoke(grantId);
      successMessage = "App access revoked successfully.";
      clearMessages();
    } catch (err) {
      errorMessage = "Failed to revoke access. Please try again.";
      clearMessages();
    } finally {
      isRevoking = null;
    }
  }
</script>

<div class="space-y-4">
  <div class="flex items-start justify-between gap-4">
    <div class="space-y-1">
      <h2 class="text-lg font-semibold tracking-tight">Connected Apps</h2>
      <p class="text-sm text-muted-foreground">
        OAuth applications that have been authorized to access your data
      </p>
    </div>
    <Button href="/oauth/device" variant="outline" size="sm" class="shrink-0">
      <Plus class="mr-1.5 h-3.5 w-3.5" />
      Add device
    </Button>
  </div>

  {#if errorMessage}
    <div
      class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
    >
      <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
      <p class="text-sm text-destructive">{errorMessage}</p>
    </div>
  {/if}

  {#if successMessage}
    <div
      class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20"
    >
      <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400" />
      <p class="text-sm text-green-800 dark:text-green-200">
        {successMessage}
      </p>
    </div>
  {/if}

  {#if apps.length === 0}
    <Card.Root>
      <Card.Content
        class="flex flex-col items-center justify-center py-12 text-center"
      >
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
        >
          <Shield class="h-6 w-6 text-muted-foreground" />
        </div>
        <p class="text-sm text-muted-foreground max-w-sm">
          No connected applications. When you authorize apps to access your
          data, they will appear here.
        </p>
      </Card.Content>
    </Card.Root>
  {:else}
    {#each apps as app (app.grantId)}
      <Card.Root>
        <Card.Header>
          <div class="flex items-start justify-between gap-4">
            <div class="space-y-1 flex-1 min-w-0">
              <Card.Title class="flex items-center gap-2 flex-wrap">
                <span class="truncate">
                  {app.clientName ?? app.clientId}
                </span>
                {#if app.isVerified}
                  <Badge variant="secondary" class="shrink-0">
                    <BadgeCheck class="mr-1 h-3 w-3" />
                    Verified
                  </Badge>
                {:else}
                  <Badge variant="outline" class="shrink-0 text-xs">
                    Self-registered
                  </Badge>
                {/if}
              </Card.Title>
              {#if app.label}
                <Card.Description>{app.label}</Card.Description>
              {/if}
              {#if app.clientUri}
                <Card.Description>
                  <a
                    href={app.clientUri}
                    target="_blank"
                    rel="noopener noreferrer"
                    class="inline-flex items-center gap-1 text-xs hover:underline"
                  >
                    {app.clientUri}
                    <ExternalLink class="h-3 w-3" />
                  </a>
                </Card.Description>
              {/if}
            </div>
            <AlertDialog.Root>
              <AlertDialog.Trigger>
                {#snippet child({ props })}
                  <Button
                    {...props}
                    type="button"
                    variant="outline"
                    size="sm"
                    class="text-destructive border-destructive/30 hover:bg-destructive/10 shrink-0"
                    disabled={isRevoking === app.grantId}
                  >
                    {#if isRevoking === app.grantId}
                      <LoaderCircle class="mr-1.5 h-3.5 w-3.5 animate-spin" />
                    {:else}
                      <Trash2 class="mr-1.5 h-3.5 w-3.5" />
                    {/if}
                    Revoke
                  </Button>
                {/snippet}
              </AlertDialog.Trigger>
              <AlertDialog.Content>
                <AlertDialog.Header>
                  <AlertDialog.Title>Revoke access</AlertDialog.Title>
                  <AlertDialog.Description>
                    Revoke {app.clientName ?? "this app"}'s access to your data?
                    The app will need to be re-authorized to regain access.
                  </AlertDialog.Description>
                </AlertDialog.Header>
                <AlertDialog.Footer>
                  <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                  <AlertDialog.Action
                    onclick={() => handleRevoke(app.grantId!)}
                  >
                    Revoke
                  </AlertDialog.Action>
                </AlertDialog.Footer>
              </AlertDialog.Content>
            </AlertDialog.Root>
          </div>
        </Card.Header>
        <Card.Content class="space-y-4">
          <div>
            <p
              class="mb-2 text-xs font-medium text-muted-foreground uppercase tracking-wider"
            >
              Permissions
            </p>
            <ul class="space-y-1.5">
              {#each app.scopes ?? [] as scope}
                <li class="flex items-start gap-2 text-sm">
                  <Check class="mt-0.5 h-3.5 w-3.5 shrink-0 text-primary" />
                  <span class="text-muted-foreground">
                    {getOAuthScopeDescription(scope)}
                  </span>
                </li>
              {/each}
            </ul>
          </div>

          <Separator />

          <div
            class="flex flex-wrap gap-x-6 gap-y-1 text-xs text-muted-foreground"
          >
            <span class="flex items-center gap-1.5">
              <Clock class="h-3 w-3" />
              Created {formatDate(app.createdAt)}
            </span>
            {#if app.lastUsedAt}
              <span class="flex items-center gap-1.5">
                <Clock class="h-3 w-3" />
                Last used {formatDate(app.lastUsedAt)}
              </span>
            {/if}
          </div>
        </Card.Content>
      </Card.Root>
    {/each}
  {/if}
</div>
