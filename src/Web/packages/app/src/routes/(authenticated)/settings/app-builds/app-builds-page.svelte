<script lang="ts">
  import { untrack } from "svelte";
  import { page } from "$app/state";
  import { afterNavigate, replaceState } from "$app/navigation";
  import Hammer from "@lucide/svelte/icons/hammer";
  import CircleCheck from "@lucide/svelte/icons/circle-check";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import { docsUrl } from "#lib/docs-url.js";
  import { getAppBuildsStatus } from "#lib/api/generated/appBuilds.generated.remote.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import {
    callbackErrorText,
    describeError,
    linkedText,
  } from "./app-builds-copy.svelte";
  import AppBuildTarget from "./AppBuildTarget.svelte";

  interface Props {
    /** The GitHub callback's `?linked=` outcome. */
    linked?: string | null;
    /** The GitHub callback's `?error=` code. */
    error?: string | null;
  }

  let { linked = null, error = null }: Props = $props();

  const canManage = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "tenant.settings")
  );

  const statusQuery = $derived(canManage ? getAppBuildsStatus() : null);
  retainQuery(() => statusQuery);
  const status = $derived(statusQuery?.current);
  const statusError = $derived(statusQuery?.error);

  // Read once: the query is cleared on mount, and the banner goes on a disconnect.
  const outcome = untrack(() => ({ linked, error }));
  let dismissed = $state(false);
  const showLinked = $derived(!dismissed && outcome.linked === "success");
  const callbackError = $derived(
    !dismissed && outcome.error ? callbackErrorText(outcome.error) : null
  );

  // afterNavigate, not onMount: on a full load it runs once the router has hydrated, and
  // replaceState before that rejects in development.
  afterNavigate(() => {
    if (!outcome.linked && !outcome.error) return;
    const url = new URL(page.url.href);
    if (
      !["linked", "error", "target"].some((name) => url.searchParams.has(name))
    )
      return;
    for (const name of ["linked", "error", "target"])
      url.searchParams.delete(name);
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- the current page's URL minus its query, already resolved
    void replaceState(url, page.state)?.catch(() => {});
  });
</script>

<svelte:head>
  <title>App builds - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl space-y-6 p-3 @md:p-6">
  <div class="flex items-center gap-3">
    <div
      class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
    >
      <Hammer class="h-5 w-5 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">App builds</h1>
      <p class="text-muted-foreground">
        Build an app in your own GitHub repository, signed with your own key.
      </p>
    </div>
  </div>

  {#if showLinked}
    <Alert.Root variant="success">
      <CircleCheck />
      <Alert.Description>{linkedText()}</Alert.Description>
    </Alert.Root>
  {/if}
  {#if callbackError}
    <Alert.Root variant="destructive">
      <TriangleAlert />
      <Alert.Description>{callbackError}</Alert.Description>
    </Alert.Root>
  {/if}

  {#if !canManage}
    <p class="text-muted-foreground">
      You must have permission to change the site settings to use app builds.
    </p>
  {:else if statusError}
    <Alert.Root variant="destructive">
      <TriangleAlert />
      <Alert.Description>{describeError(statusError)}</Alert.Description>
    </Alert.Root>
  {:else if !status}
    <p class="text-muted-foreground">Loading…</p>
  {:else if !status.configured}
    <div class="space-y-2 rounded-lg border p-4">
      <p>This Nocturne server has no GitHub app for app builds.</p>
      <!-- eslint-disable svelte/no-navigation-without-resolve -- an absolute URL to the docs site, not an app route -->
      <a
        class="inline-block text-sm underline"
        href={docsUrl(
          "/docs/configuration/github-build-app",
          "app-builds-not-configured"
        )}
        target="_blank"
        rel="noreferrer"
      >
        How the server operator configures app builds
      </a>
      <!-- eslint-enable svelte/no-navigation-without-resolve -->
    </div>
  {:else}
    {#each status.targets ?? [] as target (target.id)}
      <AppBuildTarget
        {target}
        link={status.links?.find(
          (candidate) => candidate.targetId === target.id
        )}
        onUnlinked={() => (dismissed = true)}
      />
    {/each}
  {/if}
</div>
