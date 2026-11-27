<script lang="ts">
  import {
    deletePeopleView,
    reorderPeopleViews,
    updatePeopleView,
  } from "#lib/api/generated/peopleViews.generated.remote.js";
  import type { PeopleView, PeopleViewSummary } from "@nocturne/api-client";
  import { Button } from "#lib/components/ui/button/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import Pencil from "@lucide/svelte/icons/pencil";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import { VIEW_NAME_MAX_LENGTH, isNameTaken, viewProblemMessages } from "./people-views.svelte.js";
  import { shownOfView, viewRequest } from "./people-query.js";

  interface Props {
    open: boolean;
    /** The viewer's views, in their order. */
    views: PeopleViewSummary[];
    /** The most views the viewer may hold. */
    limit: number;
    /** Called as a delete starts: the list the delete refreshes may no longer hold the view before it ends. */
    onDeleting: (viewId: string) => void;
    /** Called as a delete ends, with whether the view is gone. */
    onDeleted: (viewId: string, gone: boolean) => void;
  }

  let { open = $bindable(), views, limit, onDeleting, onDeleted }: Props = $props();

  /** The view being renamed, by id. */
  let renaming = $state<string | null>(null);
  let name = $state("");
  let busy = $state(false);
  /** Why the last command was refused, shown at the rename that made it or under the list. */
  let problem = $state<{ message: string; nameTaken: boolean; atRename: boolean } | null>(null);
  let pendingDelete = $state<PeopleView | null>(null);

  async function run(failure: string, command: () => Promise<unknown>, atRename = false): Promise<boolean> {
    if (busy) return false;
    busy = true;
    problem = null;
    try {
      await command();
      return true;
    } catch (err) {
      const message = describeSubmitError(err, failure, viewProblemMessages(limit));
      problem = { message, nameTaken: isNameTaken(err), atRename };
      return false;
    } finally {
      busy = false;
    }
  }

  function startRename(view: PeopleView) {
    problem = null;
    renaming = view.id ?? null;
    name = view.name ?? "";
  }

  async function rename(event: SubmitEvent, view: PeopleView) {
    event.preventDefault();
    const viewId = view.id;
    if (!viewId || name.trim() === "") return;
    const request = viewRequest(name, shownOfView(view));
    if (await run("Could not rename the view. Try again.", () => updatePeopleView({ viewId, request }), true)) {
      renaming = null;
    }
  }

  async function move(index: number, by: -1 | 1) {
    const ids = views.flatMap((summary) => (summary.view.id ? [summary.view.id] : []));
    const target = index + by;
    if (target < 0 || target >= ids.length) return;
    [ids[index], ids[target]] = [ids[target], ids[index]];
    await run("Could not move the view. Try again.", () => reorderPeopleViews({ viewIds: ids }));
  }

  async function confirmDelete() {
    const viewId = pendingDelete?.id;
    if (!viewId) return;
    onDeleting(viewId);
    const gone = await run("Could not delete the view. Try again.", () => deletePeopleView(viewId));
    if (gone) pendingDelete = null;
    onDeleted(viewId, gone);
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-h-[85vh] overflow-y-auto sm:max-w-lg" data-testid="people-views-manager">
    <Dialog.Header>
      <Dialog.Title>Your views</Dialog.Title>
      <Dialog.Description>Only you see your views. The tabs show them in this order.</Dialog.Description>
    </Dialog.Header>

    {#if views.length === 0}
      <p class="text-sm text-muted-foreground">You have no views.</p>
    {:else}
      <ol class="divide-y" aria-label="Your views">
        {#each views as summary, index (summary.view.id)}
          {@const view = summary.view}
          <li class="py-2" data-testid="people-view-row">
            {#if renaming === view.id}
              {@const renameProblem = problem?.atRename ? problem : null}
              <form class="space-y-1" onsubmit={(event) => rename(event, view)}>
                <div class="flex flex-wrap items-center gap-2">
                  <Input
                    class="min-w-0 flex-1"
                    aria-label="Name of the view"
                    maxlength={VIEW_NAME_MAX_LENGTH}
                    aria-invalid={renameProblem?.nameTaken ? true : undefined}
                    aria-describedby={renameProblem ? "people-view-rename-error" : undefined}
                    bind:value={name}
                  />
                  <Button type="submit" size="sm" disabled={busy || name.trim() === ""}>Save</Button>
                  <Button type="button" variant="ghost" size="sm" onclick={() => (renaming = null)}>Cancel</Button>
                </div>
                {#if renameProblem}
                  <p id="people-view-rename-error" class="text-sm text-destructive" role="alert">
                    {renameProblem.message}
                  </p>
                {/if}
              </form>
            {:else}
              <div class="flex items-center gap-1">
                <span class="min-w-0 flex-1 truncate text-sm">{view.name}</span>
                <Button
                  variant="ghost"
                  size="icon-sm"
                  aria-label="Move {view.name} up"
                  disabled={busy || index === 0}
                  onclick={() => move(index, -1)}
                >
                  <ArrowUp class="size-4" aria-hidden="true" />
                </Button>
                <Button
                  variant="ghost"
                  size="icon-sm"
                  aria-label="Move {view.name} down"
                  disabled={busy || index === views.length - 1}
                  onclick={() => move(index, 1)}
                >
                  <ArrowDown class="size-4" aria-hidden="true" />
                </Button>
                <Button
                  variant="ghost"
                  size="icon-sm"
                  aria-label="Rename {view.name}"
                  disabled={busy || renaming !== null}
                  onclick={() => startRename(view)}
                >
                  <Pencil class="size-4" aria-hidden="true" />
                </Button>
                <Button
                  variant="ghost"
                  size="icon-sm"
                  aria-label="Delete {view.name}"
                  disabled={busy}
                  onclick={() => {
                    problem = null;
                    pendingDelete = view;
                  }}
                >
                  <Trash2 class="size-4" aria-hidden="true" />
                </Button>
              </div>
            {/if}
          </li>
        {/each}
      </ol>
    {/if}

    {#if problem && !problem.atRename && !pendingDelete}
      <p class="text-sm text-destructive" role="alert">{problem.message}</p>
    {/if}
  </Dialog.Content>
</Dialog.Root>

<ConfirmDialog
  open={pendingDelete !== null}
  onOpenChange={(next) => {
    if (!next) pendingDelete = null;
  }}
  title="Delete the view {pendingDelete?.name}?"
  confirmLabel="Delete view"
  destructive
  {busy}
  onConfirm={confirmDelete}
>
  {#snippet description()}
    The tab of this view goes away. The people in it and their data do not change.
  {/snippet}
  {#if problem}
    <p class="text-sm text-destructive" role="alert">{problem.message}</p>
  {/if}
</ConfirmDialog>
