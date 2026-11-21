<script lang="ts">
  import { page } from "$app/state";
  import { resolve } from "$app/paths";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { getRoles } from "#lib/api/generated/roles.generated.remote.js";
  import {
    getMemberHistory,
    getMembers,
  } from "#lib/api/generated/memberInvites.generated.remote.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import MemberHistoryEntryRow from "#lib/components/members/MemberHistoryEntryRow.svelte";
  import { formatNumber } from "#lib/utils/formatting.js";
  import ChevronLeft from "@lucide/svelte/icons/chevron-left";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import History from "@lucide/svelte/icons/history";
  import ShieldAlert from "@lucide/svelte/icons/shield-alert";

  const pageSize = 25;

  const canView = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "members.manage"),
  );
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  const asked = $derived(page.url.searchParams.get("member") ?? "");
  const memberId = $derived(guid.test(asked) ? asked : undefined);

  let position = $state<{ memberId: string | undefined; offset: number }>({
    memberId: undefined,
    offset: 0,
  });
  const offset = $derived(position.memberId === memberId ? position.offset : 0);
  const moveTo = (next: number) => (position = { memberId, offset: next });

  const historyQuery = $derived(
    canView
      ? getMemberHistory({ memberSubjectId: memberId, limit: pageSize, offset })
      : null,
  );
  const membersQuery = $derived(canView ? getMembers() : null);
  const rolesQuery = $derived(canView ? getRoles() : null);
  retainQuery(() => historyQuery);
  retainQuery(() => membersQuery);
  retainQuery(() => rolesQuery);

  const entries = $derived(historyQuery?.current?.data ?? []);
  const total = $derived(historyQuery?.current?.pagination?.total ?? 0);
  const hasPrevious = $derived(offset > 0);
  const hasNext = $derived(offset + entries.length < total);
  const listedName = $derived(
    membersQuery?.current?.find((m) => m.subjectId === memberId)?.name,
  );
  const recorded = $derived(entries.find((e) => e.member?.subjectId === memberId)?.member);
  const memberName = $derived(listedName ?? recorded?.name);
</script>

<svelte:head>
  <title>Member history - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl space-y-6 p-3 @md:p-6">
  <div class="flex flex-col gap-4 @md:flex-row @md:items-start @md:justify-between">
    <div class="flex items-center gap-3">
      <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
        <History class="h-6 w-6 text-primary" />
      </div>
      <div>
        <h1 class="text-2xl font-bold tracking-tight">Member history</h1>
        <p class="text-muted-foreground">
          {#if canView && memberId}
            {#if memberName}
              Changes to {memberName}
            {:else if recorded}
              Changes to a deleted member
            {:else}
              Changes to one member
            {/if}
          {:else}
            Who gave or took access to your data, and when.
          {/if}
        </p>
      </div>
    </div>
    {#if canView && memberId}
      <Button variant="outline" size="sm" href={resolve("settings/members/history")}>
        Show all changes
      </Button>
    {/if}
  </div>

  {#if !canView}
    <div class="flex items-start gap-3 text-sm text-muted-foreground">
      <ShieldAlert class="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
      <p>You do not have access to the member history.</p>
    </div>
  {:else if historyQuery?.error}
    <div class="space-y-3">
      <p class="text-sm text-destructive">The member history did not load.</p>
      <Button variant="outline" size="sm" onclick={() => historyQuery?.refresh()}>
        Try again
      </Button>
    </div>
  {:else if historyQuery?.current}
    {#if entries.length === 0}
      <p class="text-sm text-muted-foreground">No member changes are recorded.</p>
    {:else}
      <p class="text-sm text-muted-foreground tabular-nums">
        {formatNumber(offset + 1)}–{formatNumber(offset + entries.length)} of {formatNumber(total)}
      </p>
      <ol class="divide-y rounded-xl border bg-card">
        {#each entries as entry (entry.id)}
          <MemberHistoryEntryRow {entry} roles={rolesQuery?.current ?? []} />
        {/each}
      </ol>
      {#if hasPrevious || hasNext}
        <div class="flex justify-end gap-2">
          <Button
            variant="outline"
            size="sm"
            disabled={!hasPrevious}
            onclick={() => moveTo(Math.max(0, offset - pageSize))}
          >
            <ChevronLeft class="mr-1 size-4" aria-hidden="true" />
            Newer
          </Button>
          <Button
            variant="outline"
            size="sm"
            disabled={!hasNext}
            onclick={() => moveTo(offset + pageSize)}
          >
            Older
            <ChevronRight class="ml-1 size-4" aria-hidden="true" />
          </Button>
        </div>
      {/if}
    {/if}
  {/if}
</div>
