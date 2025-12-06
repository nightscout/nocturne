<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Smartphone,
    Syringe,
    Brain,
    Sparkles,
    Bell,
    Plug,
    HeartHandshake,
    ChevronRight,
    Settings,
    User,
  } from "lucide-svelte";

  type SettingsSection = {
    id: string;
    title: string;
    description: string;
    icon: typeof Smartphone;
    href: string;
    badge?: string;
  };

  const settingsSections: SettingsSection[] = [
    {
      id: "account",
      title: "Account",
      description: "View your profile, roles, and session information",
      icon: User,
      href: "/settings/account",
    },
    {
      id: "devices",
      title: "Devices",
      description: "Manage CGM devices, pumps, and connected hardware",
      icon: Smartphone,
      href: "/settings/devices",
    },
    {
      id: "therapy",
      title: "Therapy (Profile)",
      description: "Configure insulin ratios, sensitivity factors, and targets via your Profile",
      icon: Syringe,
      href: "/profile",
    },
    {
      id: "algorithm",
      title: "Algorithm",
      description: "Tune prediction algorithms and automation settings",
      icon: Brain,
      href: "/settings/algorithm",
    },
    {
      id: "features",
      title: "Features",
      description: "Enable or disable plugins and display options",
      icon: Sparkles,
      href: "/settings/features",
    },
    {
      id: "notifications",
      title: "Notifications",
      description: "Configure alarms, alerts, and notification preferences",
      icon: Bell,
      href: "/settings/notifications",
    },
    {
      id: "services",
      title: "Services",
      description: "Connect data sources like Dexcom, Libre, and more",
      icon: Plug,
      href: "/settings/services",
      badge: "Connectors",
    },
    {
      id: "support",
      title: "Support & Community",
      description: "Get help, join the community, and share logs",
      icon: HeartHandshake,
      href: "/settings/support",
    },
  ];
</script>

<svelte:head>
  <title>Settings - Nocturne</title>
  <meta
    name="description"
    content="Configure your Nocturne diabetes management settings"
  />
</svelte:head>

<div class="container mx-auto p-6 max-w-4xl">
  <!-- Header -->
  <div class="mb-8">
    <div class="flex items-center gap-3 mb-2">
      <div
        class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
      >
        <Settings class="h-5 w-5 text-primary" />
      </div>
      <div>
        <h1 class="text-3xl font-bold tracking-tight">Settings</h1>
        <p class="text-muted-foreground">Configure your Nocturne experience</p>
      </div>
    </div>
  </div>

  <!-- Settings Grid -->
  <div class="grid gap-4 md:grid-cols-2">
    {#each settingsSections as section}
      <a href={section.href} class="block group">
        <Card
          class="h-full transition-colors hover:border-primary/50 hover:bg-accent/50"
        >
          <CardHeader class="pb-3">
            <div class="flex items-start justify-between">
              <div class="flex items-center gap-3">
                <div
                  class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10 group-hover:bg-primary/20 transition-colors"
                >
                  <section.icon class="h-5 w-5 text-primary" />
                </div>
                <div>
                  <CardTitle class="text-lg flex items-center gap-2">
                    {section.title}
                    {#if section.badge}
                      <Badge variant="secondary" class="text-xs font-normal">
                        {section.badge}
                      </Badge>
                    {/if}
                  </CardTitle>
                </div>
              </div>
              <ChevronRight
                class="h-5 w-5 text-muted-foreground group-hover:text-primary transition-colors"
              />
            </div>
          </CardHeader>
          <CardContent>
            <CardDescription class="text-sm">
              {section.description}
            </CardDescription>
          </CardContent>
        </Card>
      </a>
    {/each}
  </div>
</div>
