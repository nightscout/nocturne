<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import * as Avatar from "$lib/components/ui/avatar";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import {
    User,
    Mail,
    Shield,
    Clock,
    Key,
    LogOut,
    Settings,
  } from "lucide-svelte";
  import type { PageData } from "./$types";
  import { formatSessionExpiry } from "$lib/stores/auth-store.svelte";
  import { formatDate } from "$lib/utils/formatting";

  const { data }: { data: PageData } = $props();

  const user = $derived(data.user);

  /** Get initials from user name */
  function getInitials(name: string): string {
    return name
      .split(" ")
      .map((n) => n[0])
      .join("")
      .toUpperCase()
      .slice(0, 2);
  }

  /** Time until session expires in seconds */
  const timeUntilExpiry = $derived.by(() => {
    if (!user?.expiresAt) return null;
    const now = new Date();
    const expiresAt = new Date(user.expiresAt);
    const diff = expiresAt.getTime() - now.getTime();
    return Math.max(0, Math.floor(diff / 1000));
  });

  /** Handle logout */
  function handleLogout() {
    window.location.href = "/auth/logout";
  }
</script>

<svelte:head>
  <title>Account - Settings - Nocturne</title>
</svelte:head>

<div class="w-full py-6 space-y-6">
  {#if user}
    <div class="space-y-1">
      <h1 class="text-2xl font-bold tracking-tight">Account</h1>
      <p class="text-muted-foreground">
        View and manage your account information
      </p>
    </div>

    <!-- User Profile Card -->
    <Card.Root>
      <Card.Header>
        <div class="flex items-start gap-4">
          <Avatar.Root class="h-16 w-16">
            <Avatar.Fallback class="bg-primary/10 text-primary text-xl">
              {getInitials(user.name)}
            </Avatar.Fallback>
          </Avatar.Root>
          <div class="space-y-1 flex-1">
            <Card.Title class="text-xl">{user.name}</Card.Title>
            {#if user.email}
              <Card.Description class="flex items-center gap-2">
                <Mail class="h-4 w-4" />
                {user.email}
              </Card.Description>
            {/if}
          </div>
        </div>
      </Card.Header>
      <Card.Content class="space-y-6">
        <!-- Account Details -->
        <div class="space-y-4">
          <h3
            class="text-sm font-medium text-muted-foreground uppercase tracking-wider"
          >
            Account Details
          </h3>

          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-1">
              <p class="text-sm text-muted-foreground">Subject ID</p>
              <p class="text-sm font-mono bg-muted px-2 py-1 rounded">
                {user.subjectId}
              </p>
            </div>

            {#if user.expiresAt}
              <div class="space-y-1">
                <p class="text-sm text-muted-foreground">Session Expires</p>
                <p class="text-sm flex items-center gap-2">
                  <Clock class="h-4 w-4 text-muted-foreground" />
                  {formatDate(user.expiresAt)}
                  {#if timeUntilExpiry !== null}
                    <span class="text-muted-foreground">
                      ({formatSessionExpiry(timeUntilExpiry)})
                    </span>
                  {/if}
                </p>
              </div>
            {/if}
          </div>
        </div>

        <Separator />

        <!-- Roles -->
        <div class="space-y-4">
          <h3
            class="text-sm font-medium text-muted-foreground uppercase tracking-wider flex items-center gap-2"
          >
            <Shield class="h-4 w-4" />
            Roles
          </h3>

          {#if user.roles.length > 0}
            <div class="flex flex-wrap gap-2">
              {#each user.roles as role}
                <Badge variant="secondary" class="text-sm">
                  {role}
                </Badge>
              {/each}
            </div>
          {:else}
            <p class="text-sm text-muted-foreground">No roles assigned</p>
          {/if}
        </div>

        <Separator />

        <!-- Permissions -->
        <div class="space-y-4">
          <h3
            class="text-sm font-medium text-muted-foreground uppercase tracking-wider flex items-center gap-2"
          >
            <Key class="h-4 w-4" />
            Permissions
          </h3>

          {#if user.permissions.length > 0}
            <div class="flex flex-wrap gap-2">
              {#each user.permissions as permission}
                <Badge variant="outline" class="text-xs font-mono">
                  {permission}
                </Badge>
              {/each}
            </div>
          {:else}
            <p class="text-sm text-muted-foreground">No explicit permissions</p>
          {/if}
        </div>
      </Card.Content>
      <Card.Footer class="flex flex-col sm:flex-row gap-2 border-t pt-6">
        <Button variant="outline" href="/settings" class="w-full sm:w-auto">
          <Settings class="mr-2 h-4 w-4" />
          Back to Settings
        </Button>
        <Button
          variant="destructive"
          onclick={handleLogout}
          class="w-full sm:w-auto"
        >
          <LogOut class="mr-2 h-4 w-4" />
          Log Out
        </Button>
      </Card.Footer>
    </Card.Root>
  {:else}
    <!-- Not logged in -->
    <div
      class="min-h-[70vh] flex flex-col items-center justify-center p-4 animate-in fade-in slide-in-from-bottom-4 duration-500"
    >
      <Card.Root class="w-full max-w-md text-center shadow-lg">
        <Card.Header className="pb-4 pt-8">
          <div
            class="mx-auto w-16 h-16 rounded-full bg-primary/10 flex items-center justify-center mb-6"
          >
            <User class="h-8 w-8 text-primary" />
          </div>
          <Card.Title class="text-2xl font-bold">Not Signed In</Card.Title>
          <Card.Description class="text-base mt-2">
            Sign in to access your account dashboard and manage your settings.
          </Card.Description>
        </Card.Header>
        <Card.Content class="pb-8">
          <Button
            href="/auth/login"
            size="lg"
            class="w-full sm:w-auto min-w-[200px] font-medium"
          >
            <User class="mr-2 h-5 w-5" />
            Sign In with Nocturne
          </Button>
        </Card.Content>
      </Card.Root>
    </div>
  {/if}
</div>
