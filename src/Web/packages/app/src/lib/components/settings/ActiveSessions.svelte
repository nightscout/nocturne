<script lang="ts">
  import { goto } from "$app/navigation";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Monitor,
    Smartphone,
    Trash2,
    Check,
    AlertTriangle,
    Clock,
    LoaderCircle,
    LogOut,
  } from "lucide-svelte";
  import { formatDate } from "$lib/utils/formatting";
  import {
    list,
    revoke,
    revokeOthers,
  } from "$lib/api/generated/sessions.generated.remote";

  type SessionInfo = {
    sessionId?: string;
    deviceDescription?: string | null;
    ipAddress?: string | null;
    lastActiveAt?: string | Date;
    expiresAt?: string | Date;
    isCurrent?: boolean;
  };

  // Remote queries
  const sessionsQuery = list();
  const sessions = $derived<SessionInfo[]>(sessionsQuery.current ?? []);
  const otherSessionCount = $derived(
    sessions.filter((s) => !s.isCurrent).length,
  );

  // Loading/error states
  let isRevoking = $state<string | null>(null);
  let isRevokingOthers = $state(false);
  let errorMessage = $state<string | null>(null);
  let successMessage = $state<string | null>(null);

  /** Clear messages after a delay */
  function clearMessages() {
    setTimeout(() => {
      successMessage = null;
      errorMessage = null;
    }, 3000);
  }

  const MOBILE_DEVICES = ["iphone", "ipad", "android", "phone", "mobile"];

  function isMobileDevice(description: string | null | undefined): boolean {
    if (!description) return false;
    const lower = description.toLowerCase();
    return MOBILE_DEVICES.some((d) => lower.includes(d));
  }

  /** Handle revoking a session */
  async function handleRevoke(sessionId: string, isCurrent: boolean) {
    isRevoking = sessionId;
    errorMessage = null;
    try {
      await revoke(sessionId);
      if (isCurrent) {
        // Revoking the current session is a logout.
        goto("/auth/logout");
        return;
      }
      successMessage = "Session signed out.";
      clearMessages();
    } catch (err) {
      errorMessage = "Failed to sign out the session. Please try again.";
      clearMessages();
    } finally {
      isRevoking = null;
    }
  }

  /** Handle signing out everywhere else */
  async function handleRevokeOthers() {
    isRevokingOthers = true;
    errorMessage = null;
    try {
      await revokeOthers();
      successMessage = "All other sessions signed out.";
      clearMessages();
    } catch (err) {
      errorMessage = "Failed to sign out other sessions. Please try again.";
      clearMessages();
    } finally {
      isRevokingOthers = false;
    }
  }
</script>

<div class="space-y-4">
  <div class="flex items-start justify-between gap-4">
    <div class="space-y-1">
      <h2 class="text-lg font-semibold tracking-tight">Sessions</h2>
      <p class="text-sm text-muted-foreground">
        Devices currently signed in to your account
      </p>
    </div>
    {#if otherSessionCount > 0}
      <AlertDialog.Root>
        <AlertDialog.Trigger>
          {#snippet child({ props }: { props: Record<string, unknown> })}
            <Button
              {...props}
              type="button"
              variant="outline"
              size="sm"
              class="shrink-0"
              disabled={isRevokingOthers}
            >
              {#if isRevokingOthers}
                <LoaderCircle class="mr-1.5 h-3.5 w-3.5 animate-spin" />
              {:else}
                <LogOut class="mr-1.5 h-3.5 w-3.5" />
              {/if}
              Sign out everywhere else
            </Button>
          {/snippet}
        </AlertDialog.Trigger>
        <AlertDialog.Content>
          <AlertDialog.Header>
            <AlertDialog.Title>Sign out everywhere else</AlertDialog.Title>
            <AlertDialog.Description>
              Sign out {otherSessionCount}
              {otherSessionCount === 1 ? "other session" : "other sessions"}?
              This device stays signed in. The other devices will need to log
              in again.
            </AlertDialog.Description>
          </AlertDialog.Header>
          <AlertDialog.Footer>
            <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
            <AlertDialog.Action onclick={handleRevokeOthers}>
              Sign out
            </AlertDialog.Action>
          </AlertDialog.Footer>
        </AlertDialog.Content>
      </AlertDialog.Root>
    {/if}
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

  {#if sessions.length === 0}
    <Card.Root>
      <Card.Content
        class="flex flex-col items-center justify-center py-12 text-center"
      >
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
        >
          <Monitor class="h-6 w-6 text-muted-foreground" />
        </div>
        <p class="text-sm text-muted-foreground max-w-sm">
          No active sessions found.
        </p>
      </Card.Content>
    </Card.Root>
  {:else}
    {#each sessions as session (session.sessionId)}
      <Card.Root>
        <Card.Header>
          <div class="flex items-start justify-between gap-4">
            <div class="flex items-start gap-3 flex-1 min-w-0">
              <div
                class="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-muted"
              >
                {#if isMobileDevice(session.deviceDescription)}
                  <Smartphone class="h-4.5 w-4.5 text-muted-foreground" />
                {:else}
                  <Monitor class="h-4.5 w-4.5 text-muted-foreground" />
                {/if}
              </div>
              <div class="space-y-1 min-w-0">
                <Card.Title class="flex items-center gap-2 flex-wrap">
                  <span class="truncate">
                    {session.deviceDescription ?? "Unknown device"}
                  </span>
                  {#if session.isCurrent}
                    <Badge variant="secondary" class="shrink-0">
                      This device
                    </Badge>
                  {/if}
                </Card.Title>
                <Card.Description>
                  <span
                    class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground"
                  >
                    <span class="flex items-center gap-1.5">
                      <Clock class="h-3 w-3" />
                      Last active {formatDate(session.lastActiveAt)}
                    </span>
                    {#if session.ipAddress}
                      <span>IP {session.ipAddress}</span>
                    {/if}
                  </span>
                </Card.Description>
              </div>
            </div>
            <AlertDialog.Root>
              <AlertDialog.Trigger>
                {#snippet child({ props }: { props: Record<string, unknown> })}
                  <Button
                    {...props}
                    type="button"
                    variant="outline"
                    size="sm"
                    class="text-destructive border-destructive/30 hover:bg-destructive/10 shrink-0"
                    disabled={isRevoking === session.sessionId}
                  >
                    {#if isRevoking === session.sessionId}
                      <LoaderCircle class="mr-1.5 h-3.5 w-3.5 animate-spin" />
                    {:else}
                      <Trash2 class="mr-1.5 h-3.5 w-3.5" />
                    {/if}
                    Sign out
                  </Button>
                {/snippet}
              </AlertDialog.Trigger>
              <AlertDialog.Content>
                <AlertDialog.Header>
                  <AlertDialog.Title>
                    {session.isCurrent ? "Sign out this device" : "Sign out session"}
                  </AlertDialog.Title>
                  <AlertDialog.Description>
                    {#if session.isCurrent}
                      This is the session you are using now. Signing it out
                      logs you out here.
                    {:else}
                      Sign out {session.deviceDescription ?? "this device"}?
                      It will need to log in again.
                    {/if}
                  </AlertDialog.Description>
                </AlertDialog.Header>
                <AlertDialog.Footer>
                  <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                  <AlertDialog.Action
                    onclick={() =>
                      handleRevoke(session.sessionId!, session.isCurrent ?? false)}
                  >
                    Sign out
                  </AlertDialog.Action>
                </AlertDialog.Footer>
              </AlertDialog.Content>
            </AlertDialog.Root>
          </div>
        </Card.Header>
      </Card.Root>
    {/each}
  {/if}
</div>
