<script lang="ts">
  import { KeyRound, Plug, Users, ChevronRight } from "lucide-svelte";
  import * as Card from "$lib/components/ui/card";
  import ActiveSessions from "$lib/components/settings/ActiveSessions.svelte";
  import ApiReference from "$lib/components/settings/ApiReference.svelte";
  import { resolve } from "$app/paths";

  // Everything below this page's own Sessions section is managed on another
  // page. Rendering those sections here too meant Connected Apps and Guest
  // Links each existed on two pages, where revoking on one left the other
  // stale; link out instead so each has one home.
  const managedElsewhere = [
    {
      href: resolve("/settings/connectors"),
      icon: Plug,
      title: "Connectors & Apps",
      description:
        "Apps you have authorised, devices that receive your alerts, and API keys.",
    },
    {
      href: resolve("/settings/members"),
      icon: Users,
      title: "Sharing & Privacy",
      description:
        "People you have invited, temporary guest links, and your public link.",
    },
  ];
</script>

<svelte:head>
  <title>Active Access - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
  <div class="flex items-center gap-3">
    <div
      class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10"
    >
      <KeyRound class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Active Access</h1>
      <p class="text-muted-foreground">
        Everything currently able to access this account's data.
      </p>
    </div>
  </div>

  <div class="space-y-10">
    <ActiveSessions />

    <div class="space-y-4">
      <div class="space-y-1">
        <h2 class="text-lg font-semibold tracking-tight">
          What else can reach your data
        </h2>
        <p class="text-sm text-muted-foreground">
          Each of these is granted, and taken away again, on its own page.
        </p>
      </div>
      <div class="space-y-3">
        {#each managedElsewhere as entry (entry.href)}
          {@const EntryIcon = entry.icon}
          <a href={entry.href} class="group block">
            <Card.Root
              class="transition-colors hover:border-primary/40 hover:bg-muted/40"
            >
              <Card.Content class="flex items-center gap-4 p-4">
                <div
                  class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
                >
                  <EntryIcon class="h-5 w-5 text-primary" />
                </div>
                <div class="min-w-0 flex-1">
                  <p class="font-medium">{entry.title}</p>
                  <p class="text-sm text-muted-foreground">
                    {entry.description}
                  </p>
                </div>
                <ChevronRight
                  class="h-4 w-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
                />
              </Card.Content>
            </Card.Root>
          </a>
        {/each}
      </div>
    </div>

    <ApiReference />
  </div>
</div>
