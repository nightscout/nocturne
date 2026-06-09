<script lang="ts">
  import type { ComponentType } from "svelte";
  import * as Card from "$lib/components/ui/card";
  import {
    Settings,
    ListChecks,
    User,
    HeartPulse,
    Palette,
    Syringe,
    ShieldCheck,
    Timer,
    Plug,
    Globe,
    Users,
    ScrollText,
    HeartHandshake,
    Building2,
    RefreshCw,
    Shield,
    ChevronRight,
  } from "lucide-svelte";
  import type { PageData } from "./$types";

  const { data }: { data: PageData } = $props();

  const isPlatformAdmin = $derived(data.isPlatformAdmin ?? false);
  const effectivePermissions = $derived<string[]>(data.effectivePermissions ?? []);
  const canViewAudit = $derived(
    effectivePermissions.includes("audit.read") ||
      effectivePermissions.includes("audit.manage") ||
      effectivePermissions.includes("*"),
  );

  type SettingsLink = {
    title: string;
    description: string;
    href: string;
    icon: ComponentType;
  };

  // Mirrors the Settings group in the sidebar so both stay in sync.
  const sections = $derived.by(() => {
    const items: SettingsLink[] = [
      {
        title: "Account",
        description: "Profile, passkeys, authenticator apps, and recovery codes.",
        href: "/settings/account",
        icon: User,
      },
      {
        title: "Patient Record",
        description: "Details for the person being monitored.",
        href: "/settings/patient",
        icon: HeartPulse,
      },
      {
        title: "Appearance",
        description: "Theme, units, and display preferences.",
        href: "/settings/appearance",
        icon: Palette,
      },
      {
        title: "Therapy",
        description: "Targets, basal rates, ratios, and treatment profiles.",
        href: "/settings/profile",
        icon: Syringe,
      },
      {
        title: "Data Quality",
        description: "Data validation and cleanup settings.",
        href: "/settings/data-quality",
        icon: ShieldCheck,
      },
      {
        title: "Notifications & Trackers",
        description: "Alerts, reminders, and tracked events.",
        href: "/settings/trackers",
        icon: Timer,
      },
      {
        title: "Connectors & Apps",
        description: "Connect data sources and authorized devices.",
        href: "/settings/connectors",
        icon: Plug,
      },
      {
        title: "Timezone History",
        description: "Where you've lived and travelled, for correct timestamps.",
        href: "/settings/timezone",
        icon: Globe,
      },
      {
        title: "Sharing & Privacy",
        description: "Members, invitations, and public sharing.",
        href: "/settings/members",
        icon: Users,
      },
    ];
    if (canViewAudit) {
      items.push({
        title: "Audit Log",
        description: "Review changes and access history.",
        href: "/settings/audit",
        icon: ScrollText,
      });
    }
    items.push({
      title: "Support & Community",
      description: "Get help and connect with the community.",
      href: "/settings/support",
      icon: HeartHandshake,
    });
    return items;
  });

  const adminSections: SettingsLink[] = [
    {
      title: "Administration",
      description: "Identity providers and integrations.",
      href: "/settings/admin",
      icon: Shield,
    },
    {
      title: "Tenant Management",
      description: "Tenant details and platform administrators.",
      href: "/settings/admin/tenants",
      icon: Building2,
    },
    {
      title: "Reset Connector Cursors",
      description: "Re-sync a connector from a chosen point.",
      href: "/settings/admin/connector-cursors",
      icon: RefreshCw,
    },
  ];

  const onboardingSection: SettingsLink = {
    title: "Setup",
    description: "Re-run the guided onboarding checklist.",
    href: "/setup",
    icon: ListChecks,
  };
</script>

<svelte:head>
  <title>Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-8">
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
      <Settings class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Settings</h1>
      <p class="text-muted-foreground">
        Manage your account, data, and how Nocturne works for you.
      </p>
    </div>
  </div>

  <div class="grid gap-3 @md:grid-cols-2">
    {#each sections as section (section.href)}
      {@render sectionCard(section)}
    {/each}
  </div>

  {#if isPlatformAdmin}
    <div class="space-y-3">
      <h2 class="text-sm font-medium uppercase tracking-wider text-muted-foreground">
        Platform Administration
      </h2>
      <div class="grid gap-3 @md:grid-cols-2">
        {#each adminSections as section (section.href)}
          {@render sectionCard(section)}
        {/each}
      </div>
    </div>
  {/if}

  <div class="space-y-3">
    <h2 class="text-sm font-medium uppercase tracking-wider text-muted-foreground">
      Onboarding
    </h2>
    <div class="grid gap-3 @md:grid-cols-2">
      {@render sectionCard(onboardingSection)}
    </div>
  </div>
</div>

{#snippet sectionCard(section: SettingsLink)}
  <a href={section.href} class="group block">
    <Card.Root
      class="h-full transition-colors hover:border-primary/40 hover:bg-muted/40"
    >
      <Card.Content class="flex items-center gap-4 p-4">
        <div
          class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
        >
          <section.icon class="h-5 w-5 text-primary" />
        </div>
        <div class="min-w-0 flex-1">
          <p class="font-medium">{section.title}</p>
          <p class="text-sm text-muted-foreground">{section.description}</p>
        </div>
        <ChevronRight
          class="h-4 w-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
        />
      </Card.Content>
    </Card.Root>
  </a>
{/snippet}
