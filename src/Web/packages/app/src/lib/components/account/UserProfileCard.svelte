<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import * as Avatar from "$lib/components/ui/avatar";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Mail,
    Shield,
    Clock,
    Key,
    LogOut,
    Settings,
    Camera,
    Loader2,
    Trash2,
  } from "lucide-svelte";
  import { formatSessionExpiry, getAuthStore } from "$lib/stores/auth-store.svelte";
  import { formatDate } from "$lib/utils/formatting";
  import { upload as uploadAvatar, remove as deleteAvatar } from "$lib/api/generated/avatars.generated.remote";

  interface User {
    name: string;
    email?: string | null;
    subjectId: string;
    expiresAt?: string | Date | null;
    roles: string[];
    permissions: string[];
    avatarUrl?: string;
  }

  interface Props {
    user: User;
    onLogout: () => void;
  }

  const { user, onLogout }: Props = $props();

  const authStore = getAuthStore();

  let fileInput: HTMLInputElement | undefined;
  let isUploading = $state(false);
  let isDeleting = $state(false);
  let avatarError = $state<string | null>(null);

  /** Reactive avatar URL that updates after upload/delete */
  let localAvatarUrl = $state<string | undefined>();

  /** Sync localAvatarUrl when user prop changes (e.g. session reload) */
  $effect(() => {
    localAvatarUrl = user.avatarUrl;
  });

  /** Get initials from user name */
  function getInitials(name: string): string {
    return name
      .split(" ")
      .map((n) => n[0])
      .join("")
      .toUpperCase()
      .slice(0, 2);
  }

  /** Open file picker when avatar is clicked */
  function handleAvatarClick() {
    if (isUploading || isDeleting) return;
    fileInput?.click();
  }

  /** Handle file selection and upload */
  async function handleFileSelect(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    isUploading = true;
    avatarError = null;

    try {
      const result = await uploadAvatar(file) as { avatarUrl: string };
      localAvatarUrl = result.avatarUrl;
      authStore.updateAvatarUrl(result.avatarUrl);
    } catch (err) {
      avatarError = err instanceof Error ? err.message : "Failed to upload avatar";
    } finally {
      isUploading = false;
      if (fileInput) fileInput.value = "";
    }
  }

  /** Delete the current avatar */
  async function handleDeleteAvatar() {
    isDeleting = true;
    avatarError = null;

    try {
      await deleteAvatar();
      localAvatarUrl = undefined;
      authStore.updateAvatarUrl(undefined);
    } catch (err) {
      avatarError = err instanceof Error ? err.message : "Failed to delete avatar";
    } finally {
      isDeleting = false;
    }
  }

  /** Time until session expires in seconds */
  const timeUntilExpiry = $derived.by(() => {
    if (!user?.expiresAt) return null;
    const now = new Date();
    const expiresAt = new Date(user.expiresAt);
    const diff = expiresAt.getTime() - now.getTime();
    return Math.max(0, Math.floor(diff / 1000));
  });
</script>

<Card.Root>
  <Card.Header>
    <div class="flex items-start gap-4">
      <div class="relative group">
        <button
          type="button"
          class="relative rounded-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 cursor-pointer"
          onclick={handleAvatarClick}
          disabled={isUploading || isDeleting}
          title="Change avatar"
        >
          <Avatar.Root class="h-16 w-16">
            <Avatar.Image src={localAvatarUrl} alt={user.name} />
            <Avatar.Fallback class="bg-primary/10 text-primary text-xl">
              {getInitials(user.name)}
            </Avatar.Fallback>
          </Avatar.Root>
          <div class="absolute inset-0 flex items-center justify-center rounded-full bg-black/50 opacity-0 group-hover:opacity-100 transition-opacity">
            {#if isUploading}
              <Loader2 class="h-5 w-5 text-white animate-spin" />
            {:else}
              <Camera class="h-5 w-5 text-white" />
            {/if}
          </div>
        </button>
        {#if localAvatarUrl && !isUploading && !isDeleting}
          <button
            type="button"
            class="absolute -bottom-1 -right-1 flex h-6 w-6 items-center justify-center rounded-full bg-destructive text-destructive-foreground shadow-sm opacity-0 group-hover:opacity-100 transition-opacity hover:bg-destructive/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            onclick={handleDeleteAvatar}
            title="Remove avatar"
          >
            <Trash2 class="h-3 w-3" />
          </button>
        {/if}
        <input
          type="file"
          accept="image/png,image/jpeg,image/webp"
          class="hidden"
          bind:this={fileInput}
          onchange={handleFileSelect}
        />
      </div>
      <div class="space-y-1 flex-1">
        <Card.Title class="text-xl">{user.name}</Card.Title>
        {#if user.email}
          <Card.Description class="flex items-center gap-2">
            <Mail class="h-4 w-4" />
            {user.email}
          </Card.Description>
        {/if}
        {#if avatarError}
          <p class="text-xs text-destructive">{avatarError}</p>
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
      onclick={onLogout}
      class="w-full sm:w-auto"
    >
      <LogOut class="mr-2 h-4 w-4" />
      Log Out
    </Button>
  </Card.Footer>
</Card.Root>
