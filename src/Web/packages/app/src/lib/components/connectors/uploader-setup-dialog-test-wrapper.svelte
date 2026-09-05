<script lang="ts">
  import ApiTokens from "$lib/components/settings/ApiTokens.svelte";
  import UploaderSetupDialog from "./UploaderSetupDialog.svelte";
  import { createUploaderTokenHandoff } from "$routes/(authenticated)/settings/connectors/uploader-token-handoff";
  import type { UploaderApp } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    selectedUploader: UploaderApp | null;
    open?: boolean;
  }

  let { selectedUploader, open = true }: Props = $props();

  let setupOpen = $state(open);
  let tokenCreateOpen = $state(false);
  let prefillLabel = $state("");
  let prefillScopes = $state<string[]>([]);

  const uploaderHandoff = createUploaderTokenHandoff();
</script>

<!-- The connectors page's own wiring, in its own declaration order: the token dialog's portal
     anchors ahead of the uploader dialog's. -->
<ApiTokens
  bind:createOpen={tokenCreateOpen}
  {prefillLabel}
  {prefillScopes}
  onCreateClose={() => {
    if (uploaderHandoff.resumes()) setupOpen = true;
  }}
/>

<UploaderSetupDialog
  bind:open={setupOpen}
  {selectedUploader}
  onRequestApiKey={(label, scopes) => {
    prefillLabel = label;
    prefillScopes = scopes;
    uploaderHandoff.handOff();
    tokenCreateOpen = true;
  }}
/>
