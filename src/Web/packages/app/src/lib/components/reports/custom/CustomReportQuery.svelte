<!--
  Runs a spec for the viewer and hands the run to `children`. Its own component so the request is made
  only once the page has a spec to send. The run is a remote command, a POST, so a spec of any size the
  API accepts travels in the body rather than in the address.
-->
<script lang="ts">
  import { untrack, type Snippet } from "svelte";
  import { runCustomReport } from "#lib/api/generated/customReports.generated.remote.js";
  import type { CustomReportRunRequest, CustomReportRunResponse } from "#lib/api/generated/nocturne-api-client.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import LoaderCircle from "@lucide/svelte/icons/loader-circle";

  interface Props {
    request: CustomReportRunRequest;
    children: Snippet<[CustomReportRunResponse]>;
  }

  let { request, children }: Props = $props();

  let run = $state<CustomReportRunResponse | null>(null);
  let failure = $state<string | null>(null);

  $effect(() => {
    const sent = request;
    let current = true;
    run = null;
    failure = null;
    // A command's call reads and writes its own pending count; untracked, so this effect follows only `request`.
    untrack(() => runCustomReport(sent))
      .then((answer) => {
        if (current) run = answer;
      })
      .catch((err: unknown) => {
        if (current) failure = remoteErrorMessage(err, "Could not run the report. Try again.");
      });
    return () => {
      current = false;
    };
  });
</script>

{#if run}
  {@render children(run)}
{:else if failure}
  <p class="rounded-lg border border-destructive/40 bg-destructive/5 p-4 text-sm" role="alert">{failure}</p>
{:else}
  <p class="flex items-center gap-2 text-sm text-muted-foreground" role="status">
    <LoaderCircle class="size-4 animate-spin" aria-hidden="true" />
    Nocturne is running the report.
  </p>
{/if}
