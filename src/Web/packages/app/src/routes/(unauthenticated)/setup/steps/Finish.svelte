<script lang="ts" module>
  /** What the data source step actually achieved; `null` when it was skipped. */
  export type SourceResult = "connector-saved" | "uploader-receiving" | null;
  /** Where the Nightscout import got to; `null` when no run was started. */
  export type ImportResult = "running" | "complete" | "partial" | "failed" | null;
</script>

<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Item } from "$lib/components/ui/item";
  import ChartLine from "@lucide/svelte/icons/chart-line";
  import Users from "@lucide/svelte/icons/users";
  import Bell from "@lucide/svelte/icons/bell";
  import BookOpen from "@lucide/svelte/icons/book-open";
  import Plug from "@lucide/svelte/icons/plug";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import type { PatientVoice } from "$lib/onboarding/patient-voice.svelte";

  let {
    path,
    source,
    importResult,
    voice,
    onEnterDashboard,
    onNavigateWithCoach,
  }: {
    path: "fresh" | "migration";
    source: SourceResult;
    importResult: ImportResult;
    voice: PatientVoice;
    onEnterDashboard: () => void;
    onNavigateWithCoach: (url: string) => void;
  } = $props();

  const hasData = $derived(
    path === "migration" ? importResult === "complete" || importResult === "partial" : source !== null
  );

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
    path === "migration" && hasData
      ? {
          icon: BookOpen,
          title: "Your first report",
          subtitle:
            voice.kind === "self"
              ? "Generate an AGP for your next clinic visit"
              : voice.kind === "named"
                ? `Generate an AGP for ${voice.name}'s next clinic visit`
                : "Generate an AGP for the next clinic visit",
          coachUrl: "/reports?coach=setup-reports",
        }
      : {
          icon: Plug,
          title: hasData ? "Connect another source" : "Connect a data source",
          subtitle: hasData
            ? "Add another device or service"
            : "Choose a CGM, pump, or phone app",
          coachUrl: "/settings/connectors?coach=setup-connectors",
        },
  ]);
</script>

<div
  class="grid grid-cols-[1.1fr_0.9fr] max-[820px]:grid-cols-1 gap-10 items-start px-4 py-8"
>
  <div class="flex flex-col gap-8">
    <h1
      class="font-brand font-hairline text-5xl max-[820px]:text-4xl leading-tight text-foreground"
    >
      {#if path === "migration" && importResult === "complete"}
        {#if voice.kind === "self"}
          Your data is <em class="not-italic font-light text-primary">home.</em>
        {:else if voice.kind === "named"}
          {voice.name}'s data is <em class="not-italic font-light text-primary">home.</em>
        {:else}
          The data is <em class="not-italic font-light text-primary">home.</em>
        {/if}
      {:else}
        You're <em class="not-italic font-light text-primary">in.</em>
      {/if}
    </h1>

    <p class="text-lg leading-relaxed text-muted-foreground max-w-130">
      {#if path === "migration"}
        {#if importResult === "complete" && voice.kind === "self"}
          Your Nightscout history has been copied into Nocturne. Your Nightscout
          site hasn't been changed, and your uploaders keep sending to it until
          you choose to move them.
        {:else if importResult === "complete" && voice.kind === "named"}
          {voice.name}'s Nightscout history has been copied into Nocturne. The
          Nightscout site hasn't been changed, and {voice.name}'s uploaders keep
          sending to it until you choose to move them.
        {:else if importResult === "complete"}
          The Nightscout history has been copied into Nocturne. The Nightscout
          site hasn't been changed, and its uploaders keep sending to it until
          you choose to move them.
        {:else if importResult === "partial"}
          Some of the Nightscout history was copied, but not all of it. You can
          see what was missed and run the import again from Settings.
        {:else if importResult === "failed"}
          The import from Nightscout stopped before it finished, so
          some or all of the history is missing. You can see what arrived and
          try again from Settings.
        {:else if importResult === "running"}
          We lost track of your import before it finished. It may still be
          running; check Settings to see how it ended.
        {:else}
          The Nightscout history hasn't been imported yet. You can start the
          import from Settings whenever you're ready.
        {/if}
      {:else if source === "uploader-receiving"}
        The phone app is sending readings to Nocturne, so the dashboard is
        ready.
      {:else if source === "connector-saved"}
        Your data source is saved and switched on. Readings will appear on the
        dashboard after its first sync.
      {:else}
        You haven't connected a data source yet, so the dashboard will be empty
        until you do. You can connect one from Settings at any time.
      {/if}
    </p>

    <div class="flex flex-row flex-wrap items-center gap-3">
      <Button onclick={onEnterDashboard}>
        <ChartLine class="mr-2 h-4 w-4" />
        {#if voice.kind === "self"}
          Open your dashboard
        {:else if voice.kind === "named"}
          Open {voice.name}'s dashboard
        {:else}
          Open the dashboard
        {/if}
      </Button>
      <Button variant="ghost" onclick={() => onNavigateWithCoach("/?coach=quick-tour")}>Take the 60-second tour</Button>
    </div>
  </div>

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
        </Item>
      {/each}
    </div>
  </div>
</div>
