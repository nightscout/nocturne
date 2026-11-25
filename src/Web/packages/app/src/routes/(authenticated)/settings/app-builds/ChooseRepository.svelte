<script lang="ts">
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import {
    chooseAppBuildRepository,
    getAppBuildRepositories,
  } from "#lib/api/generated/appBuilds.generated.remote.js";
  import { describeError, templateName } from "./app-builds-copy.svelte";
  import ConnectRepository from "./ConnectRepository.svelte";

  interface Props {
    targetId: string;
  }

  let { targetId }: Props = $props();

  const repositoriesQuery = $derived(getAppBuildRepositories(targetId));
  const repositories = $derived(repositoriesQuery.current ?? []);
  const loaded = $derived(repositoriesQuery.current !== undefined);
  let connecting = $state(false);

  let chosen = $state("");
  let saving = $state(false);
  let error = $state<string | null>(null);

  async function choose() {
    if (!chosen) return;
    saving = true;
    error = null;
    try {
      await chooseAppBuildRepository({
        targetId,
        request: { fullName: chosen },
      });
    } catch (err) {
      error = describeError(err);
    } finally {
      saving = false;
    }
  }
</script>

<div class="space-y-3">
  {#if repositoriesQuery.error}
    <p role="alert" class="text-sm text-destructive">
      {describeError(repositoriesQuery.error)}
    </p>
  {:else if loaded && repositories.length === 0}
    <p class="text-sm">
      The Nocturne GitHub app cannot get access to a repository that you are an
      admin of. Give the app access to your {templateName(targetId)} repository on
      GitHub, then connect again.
    </p>
    {#if connecting}
      <ConnectRepository {targetId} />
    {:else}
      <Button variant="outline" onclick={() => (connecting = true)}>
        Connect again
      </Button>
    {/if}
  {:else}
    <p class="text-sm">
      The Nocturne GitHub app can get access to more than one repository. Select
      your {templateName(targetId)} repository.
    </p>
    <div class="space-y-2">
      <Label for="app-build-repository">Repository</Label>
      <Select.Root type="single" bind:value={chosen}>
        <Select.Trigger id="app-build-repository" class="w-full max-w-md">
          {chosen || "Select a repository"}
        </Select.Trigger>
        <Select.Content>
          {#each repositories as repository (repository.fullName)}
            <Select.Item
              value={repository.fullName ?? ""}
              label={repository.fullName ?? ""}
            >
              {repository.fullName}
              {#if !repository.isPrivate}
                <span class="text-muted-foreground">(public)</span>
              {/if}
            </Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
    </div>
    <Button onclick={choose} disabled={!chosen || saving}>
      {#if saving}<Loader2 class="h-4 w-4 animate-spin" />{/if}
      Use this repository
    </Button>
  {/if}
  {#if error}
    <p role="alert" class="text-sm text-destructive">{error}</p>
  {/if}
</div>
