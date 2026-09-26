<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Item } from "$lib/components/ui/item";
  import { rotateShareLink, disableShareLink } from "$api/generated/shareLinks.generated.remote";
  import { Artwork } from "@nocturne/watercolour";
  import { sproutArtwork } from "$lib/watercolour-icons";
  import {
    ChartLine,
    Users,
    Bell,
    BookOpen,
    Plug,
    ArrowRight,
    Globe,
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

  let isPublic = $state(false);
  let isToggling = $state(false);

  async function handlePublicToggle() {
    isToggling = true;
    try {
      if (isPublic) {
        await disableShareLink();
        isPublic = false;
      } else {
        await rotateShareLink();
        isPublic = true;
      }
    } finally {
      isToggling = false;
    }
  }

  const nextSteps = $derived([
    {
      icon: Users,
      title: "Invite a caretaker",
      subtitle: "Add follower access with one link",
      coachUrl: "/settings/members?coach=setup-invite",
      useBookArtwork: false,
    },
    {
      icon: Bell,
      title: "Alerts",
      subtitle: "Set up alerts",
      coachUrl: "/alerts?coach=setup-alerts",
      useBookArtwork: false,
    },
    ...(path === "migration"
      ? [
          {
            icon: BookOpen,
            title: "Your first report",
            subtitle: "Generate an AGP for your next clinic visit",
            coachUrl: "/reports?coach=setup-reports",
            useBookArtwork: true,
          },
        ]
      : [
          {
            icon: Plug,
            title: "Connect another source",
            subtitle: "Add another device or service",
            coachUrl: "/settings/connectors?coach=setup-connectors",
            useBookArtwork: false,
          },
        ]),
  ]);
</script>

<div
  class="grid grid-cols-[1.1fr_0.9fr] max-[820px]:grid-cols-1 gap-10 items-start"
>
  <!-- Left column -->
  <div class="flex flex-col gap-8">
    <!-- Celebration -->
    <div class="pulse-wrapper relative size-24">
      <Artwork
        artwork="confirmation-mark"
        palette="moss"
        surface="dark"
        motion="auto"
        autoplay="once"
        class="size-48"
      />
    </div>

    <!-- Heading -->
    <h1
      class="font-brand font-hairline text-5xl max-[820px]:text-4xl leading-tight"
    >
      {#if path === "migration"}
        Your data is <em
          class="not-italic font-light text-(--onb-accent)"
        >
          home.
        </em>
      {:else}
        You're <em
          class="not-italic font-light text-(--onb-accent)"
        >
          in.
        </em>
      {/if}
    </h1>

    {#if path === "fresh"}
      <Artwork
        icon={sproutArtwork}
        palette="moss"
        surface="dark"
        motion="auto"
        autoplay="once"
        class="size-48"
      />
    {/if}

    <!-- Lead paragraph -->
    <p class="text-lg leading-relaxed text-muted-foreground max-w-130">
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

    <!-- Public access toggle -->
    <label class="flex items-start gap-3 cursor-pointer" class:opacity-50={isToggling}>
      <Checkbox
        checked={isPublic}
        onCheckedChange={handlePublicToggle}
        disabled={isToggling}
        class="mt-0.5"
      />
      <div class="flex items-start gap-2">
        <Globe class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
        <div class="flex flex-col gap-0.5">
          <span class="text-sm text-white">Create a public share link</span>
          <span class="text-xs text-white/50">Anyone with the link can view your glucose data without signing in. Copy, rotate, or disable it later in Members settings.</span>
        </div>
      </div>
    </label>
  </div>

  <!-- Right column -->
  <div class="flex flex-col gap-4">
    <span class="text-xs uppercase tracking-widest text-muted-foreground">
      A few next things
    </span>

    <div class="flex flex-col gap-3">
      {#each nextSteps as step (step.title)}
        <Item
          variant="outline"
          class="grid grid-cols-[auto_1fr_auto]"
          onclick={() => onNavigateWithCoach(step.coachUrl)}
        >
          <div
            class="flex {step.useBookArtwork ? 'size-12' : 'h-8.5 w-8.5'} shrink-0 items-center justify-center rounded-lg text-muted-foreground"
          >
            {#if step.useBookArtwork}
              <BookOpen class="size-6 text-primary" />
            {:else}
              <step.icon class="h-4.5 w-4.5" />
            {/if}
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
        </Item>
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
