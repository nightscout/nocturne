<script lang="ts">
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import Unlink from "@lucide/svelte/icons/unlink";
  import {
    AppBuildLinkState,
    type AppBuildLinkStatus,
    type BuildTarget,
  } from "#lib/api/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import ConfirmDialog from "#lib/components/ui/confirm-dialog/confirm-dialog.svelte";
  import { unlinkAppBuild } from "#lib/api/generated/appBuilds.generated.remote.js";
  import { keystoreSecretName } from "#lib/app-builds/targets.js";
  import { formatDateTime } from "#lib/utils/formatting.js";
  import { httpsHref } from "#lib/utils/url.js";
  import {
    describeError,
    errorText,
    linkStateLabel,
    problemText,
    targetDescription,
    targetTitle,
  } from "./app-builds-copy.svelte";
  import BuildRuns from "./BuildRuns.svelte";
  import ChooseRepository from "./ChooseRepository.svelte";
  import ConnectRepository from "./ConnectRepository.svelte";
  import SigningKeyStep from "./SigningKeyStep.svelte";
  import StartBuild from "./StartBuild.svelte";

  interface Props {
    target: BuildTarget;
    link: AppBuildLinkStatus | undefined;
    onUnlinked?: () => void;
  }

  let { target, link, onUnlinked }: Props = $props();

  const targetId = $derived(target.id ?? "");
  const isLinker = $derived(link?.linkedByCaller === true);
  const secretName = $derived(keystoreSecretName(target));
  const description = $derived(targetDescription(target.id));
  const repositoryHref = $derived(httpsHref(link?.repositoryUrl));
  const needsRelink = $derived(
    link?.state === AppBuildLinkState.Invalid ||
      link?.state === AppBuildLinkState.Unreachable ||
      link?.state === AppBuildLinkState.LinkerNotAdmin
  );

  let connecting = $state(false);
  let replacingKey = $state(false);
  let keyReplaced = $state(false);
  let expectingRun = $state(false);
  let expectingTimer: ReturnType<typeof setTimeout> | undefined;

  let confirmUnlink = $state(false);
  let unlinking = $state(false);
  let unlinkError = $state<string | null>(null);
  let uninstallUrl = $state<string | undefined>(undefined);

  function buildStarted() {
    expectingRun = true;
    clearTimeout(expectingTimer);
    expectingTimer = setTimeout(() => (expectingRun = false), 2 * 60_000);
  }

  $effect(() => () => clearTimeout(expectingTimer));

  async function unlink() {
    unlinking = true;
    unlinkError = null;
    try {
      const result = await unlinkAppBuild(targetId);
      uninstallUrl = httpsHref(result.uninstallUrl);
      confirmUnlink = false;
      connecting = false;
      replacingKey = false;
      onUnlinked?.();
    } catch (err) {
      unlinkError = describeError(err);
    } finally {
      unlinking = false;
    }
  }
</script>

<Card.Root>
  <Card.Header>
    <Card.Title>{targetTitle(target.id)}</Card.Title>
    {#if description}
      <Card.Description>{description}</Card.Description>
    {/if}
  </Card.Header>
  <Card.Content class="space-y-5">
    {#if uninstallUrl}
      <div role="status" class="space-y-1 rounded-lg border p-3 text-sm">
        <p>
          Nocturne does not use the repository now. The Nocturne GitHub app
          stays installed on GitHub until you remove it there.
        </p>
        <!-- eslint-disable svelte/no-navigation-without-resolve -- a GitHub page, not an app route -->
        <a
          class="inline-flex items-center gap-1 underline"
          href={uninstallUrl}
          target="_blank"
          rel="noreferrer"
        >
          <ExternalLink class="h-3.5 w-3.5" />
          Open the GitHub app settings
        </a>
        <!-- eslint-enable svelte/no-navigation-without-resolve -->
      </div>
    {/if}

    {#if !link}
      <ConnectRepository {targetId} intro />
    {:else}
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div class="space-y-1 text-sm">
          {#if link.repositoryFullName}
            <p>
              Repository:
              <!-- eslint-disable svelte/no-navigation-without-resolve -- a GitHub page, not an app route -->
              {#if repositoryHref}
                <a
                  class="font-medium underline"
                  href={repositoryHref}
                  target="_blank"
                  rel="noreferrer"
                >
                  {link.repositoryFullName}
                </a>
              {:else}
                <span class="font-medium">{link.repositoryFullName}</span>
              {/if}
              <!-- eslint-enable svelte/no-navigation-without-resolve -->
            </p>
          {:else}
            <p>
              GitHub account: <span class="font-medium">
                {link.accountLogin}
              </span>
            </p>
          {/if}
          <p class="text-muted-foreground">
            Connected on {formatDateTime(link.linkedAt)}
          </p>
        </div>
        <Badge variant="secondary">{linkStateLabel(link.state)}</Badge>
      </div>

      {#if !isLinker}
        <p role="note" class="rounded-lg border p-3 text-sm">
          Only the person who connected this repository can change it or start a
          build. You can connect your own repository instead.
        </p>
      {/if}

      {#if link.state === AppBuildLinkState.RepositoryNotChosen}
        {#if isLinker}
          <ChooseRepository {targetId} />
        {/if}
      {:else if link.state === AppBuildLinkState.Invalid}
        <div role="alert" class="space-y-2 text-sm">
          <p>
            The repository did not pass the checks. Nocturne does not write to
            it or start builds.
          </p>
          <ul class="list-disc pl-5">
            {#each link.validation?.problems ?? [] as problem (problem)}
              <li>{problemText(problem, target.id)}</li>
            {/each}
          </ul>
        </div>
      {:else if link.state === AppBuildLinkState.Unreachable}
        <div role="alert" class="space-y-1 text-sm">
          <p>Nocturne could not read the repository on GitHub.</p>
          {#if errorText(link.error)}<p>{errorText(link.error)}</p>{/if}
        </div>
      {:else if link.state === AppBuildLinkState.LinkerNotAdmin}
        <p role="alert" class="text-sm">{errorText("linker_not_admin")}</p>
      {:else if link.state === AppBuildLinkState.SecretsMissing}
        {#if !isLinker}
          <p class="text-sm">The signing key is not saved in the repository.</p>
        {:else if secretName}
          <SigningKeyStep
            {targetId}
            {secretName}
            appName={targetTitle(target.id)}
          />
        {:else}
          <p class="text-sm">
            This page cannot save the signing key for this app.
          </p>
        {/if}
      {:else if link.state === AppBuildLinkState.Ready}
        {#if isLinker}
          <StartBuild {target} onStarted={buildStarted} />
          {#if secretName}
            {#if replacingKey}
              <div class="space-y-3 rounded-lg border p-4">
                <h3 class="font-medium">Replace the signing key</h3>
                <SigningKeyStep
                  {targetId}
                  {secretName}
                  appName={targetTitle(target.id)}
                  onSaved={() => {
                    replacingKey = false;
                    keyReplaced = true;
                  }}
                />
                <Button variant="ghost" onclick={() => (replacingKey = false)}>
                  Cancel
                </Button>
              </div>
            {:else}
              {#if keyReplaced}
                <p role="status" class="text-sm">
                  The new signing key is saved. The next build uses it.
                </p>
              {/if}
              <Button
                variant="outline"
                onclick={() => {
                  replacingKey = true;
                  keyReplaced = false;
                }}
              >
                Replace the signing key
              </Button>
            {/if}
          {/if}
        {/if}
        <BuildRuns {targetId} {expectingRun} />
      {/if}

      {#if needsRelink || !isLinker}
        {#if connecting}
          <ConnectRepository {targetId} />
        {:else}
          <Button variant="outline" onclick={() => (connecting = true)}>
            {isLinker ? "Connect again" : "Connect my own repository"}
          </Button>
        {/if}
      {/if}

      <div class="border-t pt-4">
        <Button variant="outline" onclick={() => (confirmUnlink = true)}>
          <Unlink class="h-4 w-4" />
          Disconnect the repository
        </Button>
      </div>
    {/if}
  </Card.Content>
</Card.Root>

<ConfirmDialog
  bind:open={confirmUnlink}
  title="Disconnect the repository?"
  confirmLabel="Disconnect"
  destructive
  busy={unlinking}
  onConfirm={unlink}
>
  <p class="text-sm">
    Nocturne stops using the repository. The repository, its signing key and its
    builds stay on GitHub.
  </p>
  {#if unlinkError}
    <p role="alert" class="text-sm text-destructive">{unlinkError}</p>
  {/if}
</ConfirmDialog>
