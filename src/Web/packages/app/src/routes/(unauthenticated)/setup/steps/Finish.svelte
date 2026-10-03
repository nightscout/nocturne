<script lang="ts" module>
  /** What the data source step actually achieved; `null` when it was skipped. */
  export type SourceResult = "connector-saved" | "uploader-receiving" | null;
  /** Where the Nightscout import got to; `null` when no run was started. */
  export type ImportResult = "running" | "complete" | "partial" | "failed" | null;
</script>

<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Alert from "$lib/components/ui/alert";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import type { PatientVoice } from "$lib/onboarding/patient-voice.svelte";

  let {
    path,
    source,
    importResult,
    voice,
    onContinue,
    continueFailed = false,
    onTakeTour,
  }: {
    path: "fresh" | "migration";
    source: SourceResult;
    importResult: ImportResult;
    voice: PatientVoice;
    /** Leaves the core for the setup hub. */
    onContinue: () => void;
    /** The last continue could not record the finished setup, so it is offered again. */
    continueFailed?: boolean;
    onTakeTour: () => void;
  } = $props();
</script>

<div class="px-4 py-8">
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

    {#if continueFailed}
      <Alert.Root variant="warning" data-testid="finish-failed">
        <TriangleAlert />
        <Alert.Title>We couldn't finish setup</Alert.Title>
        <Alert.Description>
          Nocturne didn't record that setup is finished, so it can't move on yet. Check your
          connection and try again.
        </Alert.Description>
      </Alert.Root>
    {/if}

    <div class="flex flex-row flex-wrap items-center gap-3">
      <Button onclick={onContinue}>
        {#if continueFailed}
          Try again
        {:else}
          Continue setting up
        {/if}
        <ArrowRight class="ml-2 h-4 w-4" />
      </Button>
      <Button variant="ghost" onclick={onTakeTour}>Take the 60-second tour</Button>
    </div>
  </div>
</div>
