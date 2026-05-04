<script lang="ts">
  import { page } from "$app/state";
  import { browser } from "$app/environment";

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
  import {
    Home,
    BarChart3,
    PieChart,
    Settings,
    Activity,
    Clock,
    User,
    ChevronDown,
    Syringe,
    Apple,
    Utensils,
    Bell,
    BellOff,
    HeartHandshake,
    Plug,
    Calendar,
    CheckCircle,
    Terminal,
    TestTube,
    Palette,
    Timer,
    Layers,
    ShieldCheck,
    Building2,
    Wrench,
    HeartPulse,
    ListChecks,
    Shield,
    ScrollText,
    Eye,
    Users,
    PlayCircle,
    History as HistoryIcon,
    SlidersHorizontal,
  } from "lucide-svelte";
  import { getSidebarReportItems } from "$lib/navigation/report-navigation";
  import type { AuthUser } from "$lib/stores/auth-store.svelte";

  interface Props {
    /** Current authenticated user (passed from layout data) */
    user?: AuthUser | null;
    /** Number of tenants the user is a member of */
    tenantCount?: number;
    /** Effective permissions for the current user */
    effectivePermissions?: string[];
    /** Whether the current user is a platform administrator */
    isPlatformAdmin?: boolean;
    /** Whether the current session is a guest link session (read-only) */
    isGuestSession?: boolean;
  }

  const { user = null, tenantCount = 0, effectivePermissions = [], isPlatformAdmin = false, isGuestSession = false }: Props = $props();

  const canManageRoles = $derived(
    effectivePermissions.includes("roles.manage") ||
      effectivePermissions.includes("*"),
  );
  const canViewAudit = $derived(
    effectivePermissions.includes("audit.read") ||
      effectivePermissions.includes("audit.manage") ||
      effectivePermissions.includes("*"),
  );
  const sidebar = Sidebar.useSidebar();

  // Tenant switcher state
  interface TenantTarget {
    id: string;
    slug: string;
    displayName: string | null;
  }
  let tenantTargets = $state<TenantTarget[]>([]);
  let totalTenantCount = $state(0);
  let selectedTenantSlug = $state<string | null>(null);
  let defaultTenantSlug = $state<string | null>(null);
  let baseDomain = $state<string | null>(null);
  let currentSlug = $state<string | null>(null);

  // Derive subdomain info from hostname
  $effect(() => {
    if (!browser) return;
    const parts = window.location.hostname.split(".");
    if (parts.length > 2 && window.location.hostname !== "localhost") {
      currentSlug = parts[0];
      baseDomain = parts.slice(1).join(".");
    }
  });

  /**
   * Fetch available tenants from the API. Only populates targets when
   * subdomain-based multitenancy is active (baseDomain is set).
   */
  async function loadTenantTargets() {
    if (!baseDomain) return;
    try {
      const tenants = await getMyTenants();
      totalTenantCount = (tenants ?? []).length;
      defaultTenantSlug = (tenants ?? [])[0]?.slug ?? null;

      tenantTargets = (tenants ?? [])
        .filter(
          (t): t is typeof t & { id: string; slug: string } =>
            !!t.id && !!t.slug && t.slug !== currentSlug,
        )
        .map((t) => ({
          id: t.id,
          slug: t.slug,
          displayName: t.displayName ?? null,
        }));

      // Pre-select based on current subdomain
      selectedTenantSlug =
        currentSlug && currentSlug !== defaultTenantSlug
          ? currentSlug
          : null;
    } catch {
      // Silently fail
    }
  }

  function handleTenantChange(value: string | undefined) {
    if (!value || !baseDomain) return;

    let targetSlug: string | null = null;
    if (value === "__self__") {
      targetSlug = currentSlug;
    } else {
      targetSlug = tenantTargets.find((t) => t.id === value)?.slug ?? null;
    }

    if (targetSlug && targetSlug !== currentSlug) {
      const host = `${targetSlug}.${baseDomain}`;
      window.location.href = `${window.location.protocol}//${host}/`;
    }
  }

  function formatTenantLabel(target: TenantTarget): string {
    return target.displayName
      ? `${target.displayName} (${target.slug})`
      : target.slug;
  }

  // Use $effect so this runs when `user` becomes available after client-side
  // login navigation (onMount alone misses that case).
  $effect(() => {
    if (user) {
      loadTenantTargets();
    }
  });

  type NavItem = {
    title: string;
    href?: string;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    icon: any;
    strict?: boolean;
    isActive?: boolean;
    children?: NavItem[];
  };

  /** Read-only navigation items shown to guest sessions. */
  const guestNavTitles = new Set(["Dashboard", "Calendar", "Time Spans", "Reports", "Clock"]);

  const navigation: NavItem[] = $derived.by(() => {
    const items: NavItem[] = [
    {
      title: "Dashboard",
      href: "/",
      icon: Home,
      strict: true,
    },
    {
      title: "Calendar",
      href: "/calendar",
      icon: Calendar,
    },
    {
      title: "Time Spans",
      href: "/time-spans",
      icon: Layers,
    },
    {
      title: "Reports",
      icon: BarChart3,
      children: [
        { title: "Overview", href: "/reports", icon: PieChart, strict: true },
        ...getSidebarReportItems(),
      ],
    },
    {
      title: "Clock",
      href: "/clock",
      icon: Clock,
    },
    ];

    // Guest sessions only see read-only navigation
    if (isGuestSession) {
      return items.filter((i) => guestNavTitles.has(i.title));
    }

    items.push(
    {
      title: "Food",
      href: "/food",
      icon: Apple,
    },
    {
      title: "Meals",
      href: "/meals",
      icon: Utensils,
    },
    {
      title: "Tools",
      icon: Wrench,
      children: [
        { title: "Packing", href: "/tools/packing", icon: Wrench },
      ],
    },
    );

    if (tenantCount >= 2) {
      items.push({
        title: "Tenants",
        href: "/tenants",
        icon: Building2,
      });
    }

    items.push(
    {
      title: "Alerts",
      icon: Bell,
      children: [
        { title: "Rules", href: "/alerts", icon: Bell, strict: true },
        { title: "Simulator", href: "/alerts/simulator", icon: PlayCircle },
        { title: "Do Not Disturb", href: "/alerts/dnd", icon: BellOff },
        { title: "History", href: "/alerts/history", icon: HistoryIcon },
      ],
    },
    {
      title: "Dev Tools",
      icon: Terminal,
      children: [
        {
          title: "Compatibility",
          href: "/compatibility",
          icon: CheckCircle,
          strict: true,
        },
        {
          title: "Test Endpoint Compatibility",
          href: "/compatibility/test",
          icon: TestTube,
        },
      ],
    },
    {
      title: "Settings",
      icon: Settings,
      children: [
        { title: "Setup", href: "/setup", icon: ListChecks },
        { title: "Account", href: "/settings/account", icon: User },
        {
          title: "Patient Record",
          href: "/settings/patient",
          icon: HeartPulse,
        },
        { title: "Appearance", href: "/settings/appearance", icon: Palette },
        { title: "Therapy", href: "/settings/profile", icon: Syringe },
        {
          title: "Data Quality",
          href: "/settings/data-quality",
          icon: ShieldCheck,
        },
        {
          title: "Notifications & Trackers",
          href: "/settings/trackers",
          icon: Timer,
        },
        { title: "Connectors & Apps", href: "/settings/connectors", icon: Plug },
        { title: "Members", href: "/settings/members", icon: Users },
        ...(canManageRoles
          ? [{ title: "Roles", href: "/settings/roles", icon: Shield }]
          : []),
        ...(canViewAudit
          ? [{ title: "Audit Log", href: "/settings/audit", icon: ScrollText }]
          : []),
        {
          title: "Support & Community",
          href: "/settings/support",
          icon: HeartHandshake,
        },
        ...(isPlatformAdmin
          ? [{ title: "Tenant Management", href: "/settings/admin/tenants", icon: Building2 }]
          : []),
      ],
    });

    return items;
  });

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
      <div
        class="flex h-8 w-8 items-center justify-center rounded-lg bg-primary"
      >
        <Activity class="h-4 w-4 text-primary-foreground" />
      </div>
      <span class="text-lg font-bold">Nocturne</span>
    </div>
    <Sidebar.Trigger />
  </Sidebar.Header>

  <!-- Glucose Widget (fixed, not scrollable) -->
  <Sidebar.Group>
    <Sidebar.GroupContent>
      <SidebarGlucoseWidget />
    </Sidebar.GroupContent>
  </Sidebar.Group>

  <Sidebar.Separator />

  <!-- Tenant switcher (only visible when multiple tenants are available, hidden for guests) -->
  {#if totalTenantCount > 1 && tenantTargets.length > 0 && !isGuestSession}
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
            {#each tenantTargets as target}
              {#if target.slug === selectedTenantSlug}
                {formatTenantLabel(target)}
              {/if}
            {/each}
          {/if}
        </Select.Trigger>
        <Select.Content>
          <Select.Item value="__self__">My Data</Select.Item>
          {#each tenantTargets as target}
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
          {#each navigation as item}
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
                    {#each item.children as child}
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
      {#if !hasLanguagePreference()}
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
        {#if user && !isGuestSession}
          <SidebarNotifications />
        {/if}
        <UserMenu
          {user}
          {isPlatformAdmin}
          collapsed={sidebar.state === "collapsed"}
          class="flex-1 min-w-0"
        />
      </Sidebar.MenuItem>
    </Sidebar.Menu>
  </Sidebar.Footer>

  <Sidebar.Rail />
</Sidebar.Sidebar>
