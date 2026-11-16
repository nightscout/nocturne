<script lang="ts">
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Play from "@lucide/svelte/icons/play";
  import { BuildInputKind, type BuildTarget } from "#lib/api/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { dispatchAppBuild } from "#lib/api/generated/appBuilds.generated.remote.js";
  import {
    describeError,
    inputHelpText,
    inputLabel,
  } from "./app-builds-copy.svelte";

  interface Props {
    target: BuildTarget;
    onStarted?: () => void;
  }

  let { target, onStarted }: Props = $props();

  const inputs = $derived(target.inputs ?? []);
  let edited = $state<Record<string, string>>({});
  const value = (name: string, fallback: string | undefined) =>
    edited[name] ?? fallback ?? "";

  let starting = $state(false);
  let started = $state(false);
  let error = $state<string | null>(null);

  async function start() {
    starting = true;
    started = false;
    error = null;
    try {
      const values = Object.fromEntries(
        inputs.map((input) => [
          input.name ?? "",
          value(input.name ?? "", input.default),
        ])
      );
      await dispatchAppBuild({
        targetId: target.id ?? "",
        request: { inputs: values },
      });
      started = true;
      onStarted?.();
    } catch (err) {
      error = describeError(err);
    } finally {
      starting = false;
    }
  }
</script>

<div class="space-y-4">
  <div class="grid gap-4 @md:grid-cols-2">
    {#each inputs as input (input.name)}
      {@const id = `app-build-input-${input.name}`}
      {@const help = inputHelpText(target.id, input.name)}
      <div class="space-y-2">
        <Label for={id}>{inputLabel(target.id, input.name)}</Label>
        {#if input.kind === BuildInputKind.Choice}
          <Select.Root
            type="single"
            value={value(input.name ?? "", input.default)}
            onValueChange={(next) => (edited[input.name ?? ""] = next)}
          >
            <Select.Trigger {id} class="w-full">
              {value(input.name ?? "", input.default)}
            </Select.Trigger>
            <Select.Content>
              {#each input.options ?? [] as option (option)}
                <Select.Item value={option} label={option} />
              {/each}
            </Select.Content>
          </Select.Root>
        {:else}
          <Input
            {id}
            value={value(input.name ?? "", input.default)}
            oninput={(event: Event & { currentTarget: HTMLInputElement }) =>
              (edited[input.name ?? ""] = event.currentTarget.value)}
            autocomplete="off"
            spellcheck={false}
          />
        {/if}
        {#if help}
          <p class="text-xs text-muted-foreground">{help}</p>
        {/if}
      </div>
    {/each}
  </div>

  <Button onclick={start} disabled={starting}>
    {#if starting}<Loader2 class="h-4 w-4 animate-spin" />{:else}<Play
        class="h-4 w-4"
      />{/if}
    Start build
  </Button>

  {#if started}
    <p role="status" class="text-sm">
      The build started. It shows in the list below in about one minute.
    </p>
  {/if}
  {#if error}
    <p role="alert" class="text-sm text-destructive">{error}</p>
  {/if}
</div>
