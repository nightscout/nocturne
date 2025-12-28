<script lang="ts">
  import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
  import * as Avatar from "$lib/components/ui/avatar";
  import { Button } from "$lib/components/ui/button";
  import { User, LogOut, Settings, Shield, ChevronDown } from "lucide-svelte";
  import { goto } from "$app/navigation";
  import type { AuthUser } from "$lib/stores/auth-store.svelte";

  interface Props {
    user: AuthUser | null;
    /** Show collapsed version (icon only) */
    collapsed?: boolean;
    /** Additional CSS classes */
    class?: string;
  }

  const { user, collapsed = false, class: className = "" }: Props = $props();

  let isOpen = $state(false);

  /** Get initials from user name */
  function getInitials(name: string): string {
    return name
      .split(" ")
      .map((n) => n[0])
      .join("")
      .toUpperCase()
      .slice(0, 2);
  }

  /** Handle logout */
  function handleLogout() {
    goto("/auth/logout");
  }
</script>

{#if user}
  <DropdownMenu.Root bind:open={isOpen}>
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        <Button
          variant="ghost"
          class="w-full justify-start gap-2 px-2 {collapsed
            ? 'justify-center'
            : ''} {className}"
          {...props}
        >
          <Avatar.Root class="h-8 w-8">
            <Avatar.Fallback class="bg-primary/10 text-primary text-xs">
              {getInitials(user.name)}
            </Avatar.Fallback>
          </Avatar.Root>
          {#if !collapsed}
            <div class="flex flex-col items-start text-left flex-1 min-w-0">
              <span class="text-sm font-medium truncate w-full">
                {user.name}
              </span>
              {#if user.email}
                <span class="text-xs text-muted-foreground truncate w-full">
                  {user.email}
                </span>
              {/if}
            </div>
            <ChevronDown class="h-4 w-4 text-muted-foreground shrink-0" />
          {/if}
        </Button>
      {/snippet}
    </DropdownMenu.Trigger>

    <DropdownMenu.Content
      class="w-56"
      align={collapsed ? "center" : "end"}
      side="top"
    >
      <DropdownMenu.Label class="font-normal">
        <div class="flex flex-col space-y-1">
          <p class="text-sm font-medium leading-none">{user.name}</p>
          {#if user.email}
            <p class="text-xs leading-none text-muted-foreground">
              {user.email}
            </p>
          {/if}
        </div>
      </DropdownMenu.Label>
      <DropdownMenu.Separator />

      {#if user.roles.length > 0}
        <DropdownMenu.Group>
          <DropdownMenu.Label class="text-xs text-muted-foreground">
            Roles
          </DropdownMenu.Label>
          <div class="px-2 py-1 flex flex-wrap gap-1">
            {#each user.roles as role}
              <span
                class="inline-flex items-center rounded-md bg-primary/10 px-2 py-0.5 text-xs font-medium text-primary"
              >
                {role}
              </span>
            {/each}
          </div>
        </DropdownMenu.Group>
        <DropdownMenu.Separator />
      {/if}

      <DropdownMenu.Group>
        <DropdownMenu.Item onSelect={() => goto("/account")}>
          <User class="mr-2 h-4 w-4" />
          <span>Account</span>
        </DropdownMenu.Item>
        <DropdownMenu.Item onSelect={() => goto("/settings")}>
          <Settings class="mr-2 h-4 w-4" />
          <span>Settings</span>
        </DropdownMenu.Item>
        {#if user.roles.includes("admin")}
          <DropdownMenu.Item onSelect={() => goto("/settings/admin")}>
            <Shield class="mr-2 h-4 w-4" />
            <span>Admin</span>
          </DropdownMenu.Item>
        {/if}
      </DropdownMenu.Group>

      <DropdownMenu.Separator />

      <DropdownMenu.Item
        onclick={handleLogout}
        class="text-destructive focus:text-destructive"
      >
        <LogOut class="mr-2 h-4 w-4" />
        <span>Log out</span>
      </DropdownMenu.Item>
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{:else}
  <!-- Not logged in - show login button -->
  <Button
    variant="ghost"
    href="/auth/login"
    class="w-full justify-start gap-2 px-2 {collapsed
      ? 'justify-center'
      : ''} {className}"
  >
    <div class="flex h-8 w-8 items-center justify-center rounded-full bg-muted">
      <User class="h-4 w-4 text-muted-foreground" />
    </div>
    {#if !collapsed}
      <span class="text-sm">Sign in</span>
    {/if}
  </Button>
{/if}
