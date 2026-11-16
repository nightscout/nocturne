<script lang="ts">
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import type { AppBuildLinkStart } from "#lib/api/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { beginAppBuildLink } from "#lib/api/generated/appBuilds.generated.remote.js";
  import { httpsHref } from "#lib/utils/url.js";
  import { describeError, templateName } from "./app-builds-copy.svelte";

  interface Props {
    targetId: string;
    /** Show what app builds are and who owns what. */
    intro?: boolean;
  }

  let { targetId, intro = false }: Props = $props();

  let start = $state<AppBuildLinkStart | null>(null);
  const createHref = $derived(httpsHref(start?.createRepositoryUrl));
  const installHref = $derived(httpsHref(start?.installUrl));
  const template = $derived(templateName(targetId));
  let loading = $state(false);
  let error = $state<string | null>(null);

  async function begin() {
    loading = true;
    error = null;
    try {
      start = await beginAppBuildLink(targetId);
    } catch (err) {
      error = describeError(err);
    } finally {
      loading = false;
    }
  }
</script>

<div class="space-y-4">
  {#if intro}
    <div class="space-y-2 text-sm">
      <p>
        The build runs in a private GitHub repository that you own, and the app
        is signed with your own key. Nocturne starts the build and shows its
        progress.
      </p>
      <p>
        Your browser locks the signing key before it sends it. The Nocturne
        server does not receive the key or its passwords. The Nocturne GitHub
        app has no permission to change the code in your repository.
      </p>
    </div>
  {/if}

  {#if !start}
    <Button onclick={begin} disabled={loading}>
      {#if loading}<Loader2 class="h-4 w-4 animate-spin" />{/if}
      Connect a repository
    </Button>
  {:else if !createHref || !installHref}
    <p role="alert" class="text-sm text-destructive">
      Nocturne received a GitHub address that is not secure. Reload the page and
      try again.
    </p>
  {:else}
    <ol class="list-decimal space-y-4 pl-5 text-sm">
      <li class="space-y-2">
        <p>
          Make your private copy of the {template} template on GitHub. If you already
          have a {template} repository, go to step 2.
        </p>
        <Button
          variant="outline"
          href={createHref}
          target="_blank"
          rel="noreferrer"
        >
          <ExternalLink class="h-4 w-4" />
          Make the repository
        </Button>
      </li>
      <li class="space-y-2">
        <p>
          Install the Nocturne GitHub app on that repository only, and approve
          the request. GitHub then sends you back to this page.
        </p>
        <Button href={installHref}>Connect the repository</Button>
      </li>
    </ol>
  {/if}

  {#if error}
    <p role="alert" class="text-sm text-destructive">{error}</p>
  {/if}
</div>
