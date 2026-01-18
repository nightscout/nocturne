<script lang="ts">
  import { page } from "$app/state";
  import * as Sidebar from "$lib/components/ui/sidebar";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import {
    SidebarGlucoseWidget,
    SidebarNotifications,
    UserMenu,
  } from "./index";
  import LanguageSelector from "$lib/components/LanguageSelector.svelte";
  import { updateLanguagePreference } from "$lib/data/user-preferences.remote";
  import { hasLanguagePreference } from "$lib/stores/appearance-store.svelte";
  import {
    Home,
    BarChart3,
    FileText,
    Settings,
    Activity,
    Clock,
    User,
    ChevronDown,
    Syringe,
    LineChart,
    PieChart,
    TrendingUp,
    Droplets,
    Apple,
    Utensils,
    Bell,
    Brain,
    HeartHandshake,
    Plug,
    Smartphone,
    Sparkles,
    Calendar,
    BatteryFull,
    Sunrise,
    CheckCircle,
    Terminal,
    TestTube,
    Palette,
    Timer,
    Layers,
  } from "lucide-svelte";
  import type { AuthUser } from "$lib/stores/auth-store.svelte";

  interface Props {
    /** Current authenticated user (passed from layout data) */
    user?: AuthUser | null;
  }

  const { user = null }: Props = $props();
  const sidebar = Sidebar.useSidebar();

  type NavItem = {
    title: string;
    href?: string;
    icon: typeof Home;
    isActive?: boolean;
    children?: NavItem[];
  };

  const navigation: NavItem[] = [
    {
      title: "Dashboard",
      href: "/",
      icon: Home,
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
        { title: "Overview", href: "/reports", icon: PieChart },
        { title: "AGP", href: "/reports/agp", icon: LineChart },
        {
          title: "Executive Summary",
          href: "/reports/executive-summary",
          icon: FileText,
        },
        { title: "Readings", href: "/reports/readings", icon: Activity },
        { title: "Treatments", href: "/reports/treatments", icon: Syringe },
        {
          title: "Insulin Delivery",
          href: "/reports/insulin-delivery",
          icon: Droplets,
        },
        {
          title: "Basal Analysis",
          href: "/reports/basal-analysis",
          icon: TrendingUp,
        },
        {
          title: "Battery",
          href: "/reports/battery",
          icon: BatteryFull,
        },
        {
          title: "Day in Review",
          href: "/reports/day-in-review",
          icon: Clock,
        },
        {
          title: "Week to Week",
          href: "/reports/week-to-week",
          icon: Sunrise,
        },
        {
          title: "Glucose Distribution",
          href: "/reports/glucose-distribution",
          icon: PieChart,
        },
        {
          title: "Site Change Impact",
          href: "/reports/site-change-impact",
          icon: Syringe,
        },
      ],
    },
    {
      title: "Clock",
      href: "/clock",
      icon: Clock,
    },
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
      title: "Profile",
      href: "/profile",
      icon: User,
    },
    {
      title: "Dev Tools",
      icon: Terminal,
      children: [
        {
          title: "Compatibility",
          href: "/compatibility",
          icon: CheckCircle,
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
        { title: "Account", href: "/settings/account", icon: User },
        { title: "Appearance", href: "/settings/appearance", icon: Palette },
        { title: "Devices", href: "/settings/devices", icon: Smartphone },
        { title: "Therapy", href: "/profile", icon: Syringe }, // Redirects to Profile page
        { title: "Algorithm", href: "/settings/algorithm", icon: Brain },
        { title: "Features", href: "/settings/features", icon: Sparkles },
        { title: "Alarms", href: "/settings/alarms", icon: Bell },
        {
          title: "Notifications & Trackers",
          href: "/settings/trackers",
          icon: Timer,
        },
        { title: "Services", href: "/settings/services", icon: Plug },
        {
          title: "Support & Community",
          href: "/settings/support",
          icon: HeartHandshake,
        },
      ],
    },
  ];

  // Track which collapsible menus are open
  let openMenus = $state<Record<string, boolean>>({});

  // Check if current path matches or starts with a nav item path
  const isActive = (item: NavItem): boolean => {
    if (item.href) {
      if (item.href === "/") {
        return page.url.pathname === "/";
      }
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
                        <Sidebar.MenuSubButton
                          href={child.href}
                          isActive={isActive(child)}
                        >
                          <child.icon class="h-4 w-4" />
                          <span>{child.title}</span>
                        </Sidebar.MenuSubButton>
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
            onLanguageChange={user ? updateLanguagePreference : undefined}
          />
        </Sidebar.MenuItem>
      {/if}
      <Sidebar.MenuItem
        class="flex items-center gap-2 min-w-0 group-data-[collapsible=icon]:flex-col"
      >
        <SidebarNotifications />
        <UserMenu
          {user}
          collapsed={sidebar.state === "collapsed"}
          class="flex-1 min-w-0"
        />
      </Sidebar.MenuItem>
    </Sidebar.Menu>
  </Sidebar.Footer>

  <Sidebar.Rail />
</Sidebar.Sidebar>
