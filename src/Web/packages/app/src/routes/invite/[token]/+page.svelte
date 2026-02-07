<script lang="ts">
  import { enhance } from "$app/forms";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import {
    UserPlus,
    Check,
    AlertTriangle,
    Clock,
    Eye,
    Activity,
    Smartphone,
    User,
    Bell,
    FileText,
  } from "lucide-svelte";

  const { data, form } = $props();

  /** Human-readable descriptions for each OAuth scope. */
  const scopeDescriptions: Record<string, string> = {
    "entries.read": "View glucose readings",
    "treatments.read": "View treatments",
    "devicestatus.read": "View device status",
    "profile.read": "View profile settings",
    "notifications.read": "View notifications",
    "reports.read": "View reports and analytics",
    "identity.read": "View basic account info",
    "health.read": "View all health data (read-only)",
  };

  /** Icons for scopes */
  const scopeIcons: Record<string, typeof Eye> = {
    "entries.read": Activity,
    "treatments.read": FileText,
    "devicestatus.read": Smartphone,
    "profile.read": User,
    "notifications.read": Bell,
    "reports.read": FileText,
  };

  const invite = $derived(data.invite);
  const isAuthenticated = $derived(data.isAuthenticated);
  const formError = $derived(form?.error as string | undefined);
</script>

<svelte:head>
  <title>Accept Invite - Nocturne</title>
</svelte:head>

<div class="flex min-h-screen items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    {#if !invite}
      <!-- Invite not found -->
      <Card.Header class="text-center">
        <div class="mx-auto mb-4 flex h-16 w-16 items-center justify-center rounded-full bg-destructive/10">
          <AlertTriangle class="h-8 w-8 text-destructive" />
        </div>
        <Card.Title class="text-xl">Invite Not Found</Card.Title>
        <Card.Description>
          {data.error ?? "This invite link is invalid or has expired."}
        </Card.Description>
      </Card.Header>
      <Card.Content class="text-center">
        <Button href="/auth/login" variant="outline">
          Go to Login
        </Button>
      </Card.Content>
    {:else if !invite.isValid}
      <!-- Invite expired or revoked -->
      <Card.Header class="text-center">
        <div class="mx-auto mb-4 flex h-16 w-16 items-center justify-center rounded-full bg-muted">
          <Clock class="h-8 w-8 text-muted-foreground" />
        </div>
        <Card.Title class="text-xl">
          {#if invite.isExpired}
            Invite Expired
          {:else if invite.isRevoked}
            Invite Revoked
          {:else}
            Invite Unavailable
          {/if}
        </Card.Title>
        <Card.Description>
          {#if invite.isExpired}
            This invite link has expired. Please ask {invite.ownerName ?? "the data owner"} for a new invite.
          {:else if invite.isRevoked}
            This invite link has been revoked by {invite.ownerName ?? "the data owner"}.
          {:else}
            This invite link is no longer available.
          {/if}
        </Card.Description>
      </Card.Header>
      <Card.Content class="text-center">
        <Button href="/auth/login" variant="outline">
          Go to Login
        </Button>
      </Card.Content>
    {:else}
      <!-- Valid invite -->
      <Card.Header class="text-center">
        <div class="mx-auto mb-4 flex h-16 w-16 items-center justify-center rounded-full bg-primary/10">
          <UserPlus class="h-8 w-8 text-primary" />
        </div>
        <Card.Title class="text-xl">You're Invited</Card.Title>
        <Card.Description>
          <span class="font-medium text-foreground">
            {invite.ownerName ?? invite.ownerEmail ?? "Someone"}
          </span>
          wants to share their health data with you
          {#if invite.label}
            <Badge variant="secondary" class="ml-2">{invite.label}</Badge>
          {/if}
        </Card.Description>
      </Card.Header>

      <Card.Content class="space-y-6">
        <!-- What you'll be able to see -->
        <div>
          <p class="mb-3 text-sm font-medium">You'll be able to see:</p>
          <ul class="space-y-2">
            {#each invite.scopes as scope}
              {@const Icon = scopeIcons[scope] ?? Eye}
              <li class="flex items-center gap-3 text-sm">
                <div class="flex h-8 w-8 items-center justify-center rounded-full bg-muted">
                  <Icon class="h-4 w-4 text-muted-foreground" />
                </div>
                <span>{scopeDescriptions[scope] ?? scope}</span>
              </li>
            {/each}
          </ul>
        </div>

        {#if formError}
          <div class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3">
            <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">{formError}</p>
          </div>
        {/if}

        {#if isAuthenticated}
          <!-- User is logged in - show accept button -->
          <form method="POST" action="?/accept" use:enhance>
            <Button type="submit" class="w-full" size="lg">
              <Check class="mr-2 h-4 w-4" />
              Accept Invite
            </Button>
          </form>
        {:else}
          <!-- User not logged in - show sign in button -->
          <div class="space-y-3">
            <p class="text-center text-sm text-muted-foreground">
              Sign in or create an account to accept this invite
            </p>
            <Button
              href="/auth/login?returnUrl=/invite/{data.token}"
              class="w-full"
              size="lg"
            >
              Sign In to Accept
            </Button>
          </div>
        {/if}

        <p class="text-center text-xs text-muted-foreground">
          This invite expires on {new Date(invite.expiresAt).toLocaleDateString()}
        </p>
      </Card.Content>
    {/if}
  </Card.Root>
</div>
