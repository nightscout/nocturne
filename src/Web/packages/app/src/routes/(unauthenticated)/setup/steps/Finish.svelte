<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import {
    Check,
    ChartLine,
    Users,
    Bell,
    BookOpen,
    Plug,
    ArrowRight,
  } from "lucide-svelte";

  let {
    path,
    onEnterDashboard,
    onNavigateWithCoach,
  }: {
    path: "fresh" | "migration";
    onEnterDashboard: () => void;
    onNavigateWithCoach: (url: string) => void;
  } = $props();

  const nextSteps = $derived([
    {
      icon: Users,
      title: "Invite a caretaker",
      subtitle: "Add follower access with one link",
      coachUrl: "/settings/members?coach=setup-invite",
    },
    {
      icon: Bell,
      title: "Alerts",
      subtitle: "Set up alerts",
      coachUrl: "/alerts?coach=setup-alerts",
    },
    ...(path === "migration"
      ? [
          {
            icon: BookOpen,
            title: "Your first report",
            subtitle: "Generate an AGP for your next clinic visit",
            coachUrl: "/reports?coach=setup-reports",
          },
        ]
      : [
          {
            icon: Plug,
            title: "Connect another source",
            subtitle: "Add another device or service",
            coachUrl: "/settings/connectors?coach=setup-connectors",
          },
        ]),
  ]);
</script>

<div
  class="grid grid-cols-[1.1fr_0.9fr] max-[820px]:grid-cols-1 gap-10 items-start"
>
  <!-- Left column -->
  <div class="flex flex-col gap-8">
    <!-- Celebration checkmark -->
    <div class="pulse-wrapper relative size-24">
      <div
        class="size-24 rounded-full border-2 flex items-center justify-center relative"
        style="border-color: var(--onb-accent); background: var(--onb-accent-dim);"
      >
        <Check style="width: 40px; height: 40px; color: var(--onb-accent);" />
      </div>
    </div>

    <!-- Heading -->
    <h1
      class="font-[Montserrat] font-[250] text-[52px] max-[820px]:text-[36px] leading-tight"
    >
      {#if path === "migration"}
        Your data is <em
          class="not-italic font-light"
          style="color: var(--onb-accent);"
        >
          home.
        </em>
      {:else}
        You're <em
          class="not-italic font-light"
          style="color: var(--onb-accent);"
        >
          in.
        </em>
      {/if}
    </h1>

    <!-- Lead paragraph -->
    <p class="text-[17px] leading-relaxed text-muted-foreground max-w-130">
      {#if path === "migration"}
        All your entries, treatments, and profiles are in Nocturne. Your
        existing uploaders keep working — you don't need to change them until
        you're ready.
      {:else}
        Your CGM is connected, your target range is set, and the dashboard is
        waiting. The next reading will land any minute.
      {/if}
    </p>

    <!-- Buttons -->
    <div class="flex flex-row items-center gap-3">
      <Button onclick={onEnterDashboard}>
        <ChartLine class="mr-2 h-4 w-4" />
        Open my dashboard
      </Button>
      <Button variant="ghost" onclick={() => onNavigateWithCoach("/?coach=quick-tour")}>Take the 60-second tour</Button>
    </div>
  </div>

  <!-- Right column -->
  <div class="flex flex-col gap-4">
    <span class="text-[11px] uppercase tracking-[0.12em] text-muted-foreground">
      A few next things
    </span>

    <div class="flex flex-col gap-3">
      {#each nextSteps as step}
        <button
          class="group grid grid-cols-[34px_1fr_auto] gap-3 items-center p-3 rounded-xl border border-white/6 bg-white/3 transition-[border-color,background-color] duration-150 cursor-pointer hover:border-white/12 hover:bg-white/5"
          type="button"
          onclick={() => onNavigateWithCoach(step.coachUrl)}
        >
          <div
            class="flex h-8.5 w-8.5 shrink-0 items-center justify-center rounded-lg text-muted-foreground"
          >
            <step.icon class="h-4.5 w-4.5" />
          </div>
          <div class="flex flex-col text-left">
            <span class="text-sm font-medium">{step.title}</span>
            <span class="text-xs text-muted-foreground">{step.subtitle}</span>
          </div>
          <div
            class="ml-auto flex items-center opacity-0 transition-opacity group-hover:opacity-100"
          >
            <ArrowRight class="h-4 w-4 text-muted-foreground" />
          </div>
        </button>
      {/each}
    </div>
  </div>
</div>

<style>
  .pulse-wrapper::after {
    content: "";
    position: absolute;
    inset: 0;
    border-radius: 50%;
    border: 2px solid var(--onb-accent);
    animation: pulse-ring 2s ease-out infinite;
    pointer-events: none;
  }

  @keyframes pulse-ring {
    0% {
      transform: scale(1);
      opacity: 0.6;
    }
    100% {
      transform: scale(1.3);
      opacity: 0;
    }
  }
</style>
