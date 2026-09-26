<script lang="ts" module>
  /** What the data source step actually achieved; `null` when it was skipped. */
  export type SourceResult = "connector-saved" | "uploader-receiving" | null;
  /** How the Nightscout import ended; `null` when it never reached an end. */
  export type ImportResult = "complete" | "partial" | "failed" | null;
</script>

<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Item } from "$lib/components/ui/item";
  import {
    ChartLine,
    Users,
    Bell,
    BookOpen,
    Plug,
    ArrowRight,
  } from "lucide-svelte";

  let {
    path,
    source,
    importResult,
    onEnterDashboard,
    onNavigateWithCoach,
  }: {
    path: "fresh" | "migration";
    source: SourceResult;
    importResult: ImportResult;
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
          subtitle: "Generate an AGP for your next clinic visit",
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
        Your data is <em class="not-italic font-light text-primary">home.</em>
      {:else}
        You're <em class="not-italic font-light text-primary">in.</em>
      {/if}
    </h1>

    <p class="text-lg leading-relaxed text-muted-foreground max-w-130">
      {#if path === "migration"}
        {#if importResult === "complete"}
          Your Nightscout history has been copied into Nocturne. Your Nightscout
          site hasn't been changed, and your uploaders keep sending to it until
          you choose to move them.
        {:else if importResult === "partial"}
          Some of your Nightscout history was copied, but not all of it. You can
          see what was missed and run the import again from Settings.
        {:else if importResult === "failed"}
          The import from Nightscout didn't finish, so none of your history is
          here yet. You can try again from Settings.
        {:else}
          Your Nightscout history hasn't been imported yet. You can start the
          import from Settings whenever you're ready.
        {/if}
      {:else if source === "uploader-receiving"}
        Your phone app is sending readings to Nocturne, so your dashboard is
        ready.
      {:else if source === "connector-saved"}
        Your data source is saved and switched on. Readings will appear on your
        dashboard after its first sync.
      {:else}
        You haven't connected a data source yet, so your dashboard will be empty
        until you do. You can connect one from Settings at any time.
      {/if}
    </p>

    <div class="flex flex-row flex-wrap items-center gap-3">
      <Button onclick={onEnterDashboard}>
        <ChartLine class="mr-2 h-4 w-4" />
        Open my dashboard
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
