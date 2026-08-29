<script lang="ts">
  import { page } from "$app/state";

  import * as Sidebar from "$lib/components/ui/sidebar";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import * as Select from "$lib/components/ui/select";
  import SidebarGlucoseWidget from "./SidebarGlucoseWidget.svelte";
  import SidebarNotifications from "./SidebarNotifications.svelte";
  import SidebarDndToggle from "$lib/components/alerts/SidebarDndToggle.svelte";
  import UserMenu from "./UserMenu.svelte";
  import LanguageSelector from "$lib/components/LanguageSelector.svelte";
  import { updateLanguagePreference } from "$api/user-preferences.remote";
  import { hasLanguagePreference } from "$lib/stores/appearance-store.svelte";
  import { getMyTenants } from "$lib/api/generated/myTenants.generated.remote";
  import { ChevronDown, Shield, Eye } from "lucide-svelte";
  import { buildAppNavigation, type NavItem } from "$lib/navigation/app-navigation";
  import {
    activeTenants,
    resolveTenantSwitcher,
    tenantUrl,
    type TenantSwitcherTarget,
  } from "$lib/utils/tenant-host";
  import type { AuthUser } from "$lib/stores/auth-store.svelte";

  interface Props {
    /** Current authenticated user (passed from layout data) */
    user?: AuthUser | null;
    /** Whether the current user is a platform administrator */
    isPlatformAdmin?: boolean;
    /** Whether this session is a platform-admin access grant on a non-member tenant */
    isPlatformAccessGrant?: boolean;
    /** Whether the current session is a guest link session (read-only) */
    isGuestSession?: boolean;
    /** Slug of the tenant this host resolves to, or null on the apex (from layout data) */
    currentSlug?: string | null;
    /** Public base domain tenant subdomains hang off (from layout data) */
    baseDomain?: string | null;
    /** Whether this host serves the cross-tenant dashboard rather than one tenant (from layout data) */
    tenantless?: boolean;
  }

  const {
    user = null,
    isPlatformAdmin = false,
    isPlatformAccessGrant = false,
    isGuestSession = false,
    currentSlug = null,
    baseDomain = null,
    tenantless = false,
  }: Props = $props();

  const sidebar = Sidebar.useSidebar();

  // Defer localStorage check to after hydration so SSR and client initial render
  // both produce the same DOM (avoids hydration mismatch from conditional rendering).
  // $derived would read localStorage during hydration and reintroduce the mismatch.
  // eslint-disable-next-line svelte/prefer-writable-derived -- $effect defers the localStorage read past hydration; $derived would not
  let langPrefKnown = $state(false);
  $effect(() => {
    langPrefKnown = hasLanguagePreference();
  });

  // Tenant switcher state
  let tenantTargets = $state<TenantSwitcherTarget[]>([]);
  let totalTenantCount = $state(0);
  let selectedTenantSlug = $state<string | null>(null);

  /**
   * Platform-admin "access" mode: the session is a short-lived platform-access grant on a
   * tenant the operator is NOT a member of. This is the backend's authoritative signal
   * (AuthType.PlatformAccess) rather than an inference from the subdomain slug, so it can
   * never be mistaken for ordinary membership.
   */
  const isPlatformAccessView = $derived(isPlatformAccessGrant && !isGuestSession);
  // Minutes left on the access grant (its JWT expiry flows through to the session).
  const grantExpiresInMin = $derived(
    user?.expiresAt
      ? Math.max(
          0,
          Math.round((new Date(user.expiresAt).getTime() - Date.now()) / 60000),
        )
      : null,
  );

  // Available tenants for the subdomain switcher.
  const myTenantsQuery = getMyTenants();

  function handleTenantChange(value: string | undefined) {
    if (!value || !baseDomain) return;

    const targetSlug: string | null =
      value === "__self__"
        ? currentSlug
        : (tenantTargets.find((t) => t.id === value)?.slug ?? null);

    if (targetSlug && targetSlug !== currentSlug) {
      window.location.href = tenantUrl(targetSlug, baseDomain);
    }
  }

  function formatTenantLabel(target: TenantSwitcherTarget): string {
    return target.displayName
      ? `${target.displayName} (${target.slug})`
      : target.slug;
  }

  // Use $effect (not onMount) so this also runs when `user` becomes available
  // after client-side login navigation.
  $effect(() => {
    if (!user || isGuestSession) return;
    const tenants = myTenantsQuery.current;
    if (tenants === undefined) return;

    const switcher = resolveTenantSwitcher(tenants, currentSlug);
    totalTenantCount = switcher.totalCount;
    tenantTargets = switcher.targets;

    // Pre-select based on current subdomain; the first reachable tenant is "My Data".
    const defaultSlug = activeTenants(tenants)[0]?.slug ?? null;
    selectedTenantSlug =
      currentSlug && currentSlug !== defaultSlug ? currentSlug : null;
  });

  const navigation: NavItem[] = $derived(
    buildAppNavigation({
      user,
      isGuestSession,
      isPlatformAdmin,
      grantedScopes: page.data.effectivePermissions ?? [],
      tenantCount: totalTenantCount,
      tenantless,
    }),
  );

  // Track which collapsible menus are open
  let openMenus = $state<Record<string, boolean>>({});

  // Check if current path matches or starts with a nav item path
  // const isActive = (item: NavItem): boolean => {
  //   if (item.href) {
  //     if (item.href === "/") {
  //       return page.url.pathname === "/";
  //     }
  //     return page.url.pathname.startsWith(item.href);
  //   }
  //   if (item.children) {
  //     return item.children.some((child) => isActive(child));
  //   }
  //   return false;
  // };

  const isActive = (item: NavItem): boolean => {
    if (item.href && item?.strict) {
      return page.url.pathname === item.href;
    }

    if (item.href) {
      return page.url.pathname.startsWith(item.href);
    }

    if (item.children) {
      return item.children.some((child) => isActive(child));
    }

    return false;
  };

  // Initialize open state for menus that have active children
  $effect(() => {
    navigation.forEach((item) => {
      if (item.children && isActive(item)) {
        openMenus[item.title] = true;
      }
    });
  });

  function toggleMenu(title: string) {
    openMenus[title] = !openMenus[title];
  }
</script>

<Sidebar.Sidebar collapsible="icon">
  <Sidebar.Header
    class="flex flex-row items-center justify-between p-4 group-data-[collapsible=icon]:justify-center group-data-[collapsible=icon]:px-2"
  >
    <div class="flex items-center gap-2 group-data-[collapsible=icon]:hidden">
      <img
        src="/logos/nocturne.png"
        alt="Nocturne"
        class="h-8 w-8 rounded-lg dark:block hidden"
      />
      <img
        src="/logos/nocturne-light.png"
        alt="Nocturne"
        class="h-8 w-8 rounded-lg dark:hidden block"
      />
      <span class="text-lg font-bold">Nocturne</span>
    </div>
    <Sidebar.Trigger />
  </Sidebar.Header>

  <!-- Glucose Widget (fixed, not scrollable). One tenant's latest reading. -->
  {#if !tenantless}
    <Sidebar.Group>
      <Sidebar.GroupContent>
        <SidebarGlucoseWidget />
      </Sidebar.GroupContent>
    </Sidebar.Group>

    <Sidebar.Separator />
  {/if}

  <!-- Platform-admin access indicator: viewing a tenant you're NOT a member of,
       via a short-lived platform-access grant (distinct from the member switcher). -->
  {#if isPlatformAccessView}
    <div
      class="border-b border-amber-500/40 bg-amber-500/10 px-3 py-2 group-data-[collapsible=icon]:hidden"
    >
      <p
        class="mb-1 flex items-center gap-1.5 text-xs font-semibold text-amber-700 dark:text-amber-400"
      >
        <Shield class="h-3 w-3 shrink-0" />
        Platform admin access
      </p>
      <p class="flex items-center gap-1.5 text-xs text-muted-foreground">
        <Eye class="h-3 w-3 shrink-0" />
        <span
          >Viewing <span class="font-medium text-foreground">{currentSlug}</span> — you
          are not a member</span
        >
      </p>
      {#if grantExpiresInMin !== null && grantExpiresInMin > 0}
        <p class="mt-0.5 text-[11px] text-muted-foreground">
          Access expires in ~{grantExpiresInMin} min
        </p>
      {/if}
    </div>
  {/if}

  <!-- The switcher needs a host to navigate to, so unlike the Tenants nav item it stays gated
       on baseDomain. -->
  {#if baseDomain && totalTenantCount > 1 && tenantTargets.length > 0 && !isGuestSession}
    <div class="border-b px-3 py-2 group-data-[collapsible=icon]:hidden">
      <p
        class="mb-1.5 text-xs font-medium text-muted-foreground flex items-center gap-1.5"
      >
        <Eye class="h-3 w-3" />
        Viewing data for
      </p>
      <Select.Root
        type="single"
        value={selectedTenantSlug
          ? (tenantTargets.find((t) => t.slug === selectedTenantSlug)?.id ??
            "__self__")
          : "__self__"}
        onValueChange={handleTenantChange}
      >
        <Select.Trigger size="sm" class="w-full">
          {#if !selectedTenantSlug}
            My Data
          {:else}
            {#each tenantTargets as target (target.id)}
              {#if target.slug === selectedTenantSlug}
                {formatTenantLabel(target)}
              {/if}
            {/each}
          {/if}
        </Select.Trigger>
        <Select.Content>
          <Select.Item value="__self__">My Data</Select.Item>
          {#each tenantTargets as target (target.id)}
            <Select.Item value={target.id}>
              {formatTenantLabel(target)}
            </Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
    </div>
  {/if}

  <Sidebar.Content>
    <!-- Navigation -->
    <Sidebar.Group>
      <Sidebar.GroupLabel>Navigation</Sidebar.GroupLabel>
      <Sidebar.GroupContent>
        <Sidebar.Menu>
          {#each navigation as item (item.title)}
            {#if item.children}
              <!-- Collapsible submenu -->
              <Collapsible.Root
                open={openMenus[item.title]}
                onOpenChange={() => toggleMenu(item.title)}
              >
                <Sidebar.MenuItem>
                  <Sidebar.MenuButton
                    isActive={isActive(item)}
                    onclick={() => toggleMenu(item.title)}
                  >
                    <item.icon class="h-4 w-4" />
                    <span class="group-data-[collapsible=icon]:hidden">
                      {item.title}
                    </span>
                    <ChevronDown
                      class="ml-auto h-4 w-4 transition-transform duration-200 group-data-[collapsible=icon]:hidden {openMenus[
                        item.title
                      ]
                        ? 'rotate-180'
                        : ''}"
                    />
                  </Sidebar.MenuButton>
                </Sidebar.MenuItem>
                <Collapsible.Content>
                  <Sidebar.MenuSub>
                    {#each item.children as child (child.title)}
                      <Sidebar.MenuSubItem>
                        {#if child.href === "/alerts/dnd"}
                          <SidebarDndToggle />
                        {:else}
                          <Sidebar.MenuSubButton
                            href={child.href}
                            isActive={isActive(child)}
                          >
                            <child.icon class="h-4 w-4" />
                            <span>{child.title}</span>
                          </Sidebar.MenuSubButton>
                        {/if}
                      </Sidebar.MenuSubItem>
                    {/each}
                  </Sidebar.MenuSub>
                </Collapsible.Content>
              </Collapsible.Root>
            {:else}
              <!-- Simple menu item with link -->
              <Sidebar.MenuItem>
                <Sidebar.MenuButton isActive={isActive(item)}>
                  {#snippet child({ props })}
                    <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- item.href is a runtime string | undefined from NavItem; resolve() requires a literal route id and throws on undefined -->
                    <a href={item.href} {...props}>
                      <item.icon class="h-4 w-4" />
                      <span class="group-data-[collapsible=icon]:hidden">
                        {item.title}
                      </span>
                    </a>
                  {/snippet}
                </Sidebar.MenuButton>
              </Sidebar.MenuItem>
            {/if}
          {/each}
        </Sidebar.Menu>
      </Sidebar.GroupContent>
    </Sidebar.Group>
  </Sidebar.Content>

  <Sidebar.Footer class="p-2">
    <Sidebar.Menu>
      {#if !langPrefKnown}
        <Sidebar.MenuItem class="group-data-[collapsible=icon]:hidden">
          <LanguageSelector
            onLanguageChange={user
              ? (locale: string) =>
                  updateLanguagePreference({ preferredLanguage: locale })
              : undefined}
          />
        </Sidebar.MenuItem>
      {/if}
      <Sidebar.MenuItem
        class="flex items-center gap-2 min-w-0 group-data-[collapsible=icon]:flex-col"
      >
        {#if user && !isGuestSession && !tenantless}
          <SidebarNotifications />
        {/if}
        <UserMenu
          {user}
          {isPlatformAdmin}
          {isGuestSession}
          {tenantless}
          collapsed={sidebar.state === "collapsed"}
          class="flex-1 min-w-0"
        />
      </Sidebar.MenuItem>
    </Sidebar.Menu>
  </Sidebar.Footer>

  <Sidebar.Rail />
</Sidebar.Sidebar>
