<script lang="ts">
  import type { Snippet } from "svelte";
  import type { PeopleViewSummary, StaffGroup } from "@nocturne/api-client";
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import GroupName from "./GroupName.svelte";
  import { tabOfValue, tabValue, type PeopleTab } from "./people-tab.js";

  interface Props {
    /** The viewer's groups, in the API's order. */
    groups: StaffGroup[];
    /** The viewer's views, in their own order. */
    views: PeopleViewSummary[];
    tab: PeopleTab;
    onChange: (tab: PeopleTab) => void;
    /** What the open tab lists. */
    children: Snippet;
    /** Shown beside the strip. */
    actions?: Snippet;
  }

  const { groups, views, tab, onChange, children, actions }: Props = $props();
</script>

<Tabs.Root value={tabValue(tab)} onValueChange={(value) => onChange(tabOfValue(value))}>
  <div class="flex flex-wrap items-start gap-2">
    <Tabs.List variant="wrap" aria-label="Lists of people" class="min-w-0 flex-1">
      <Tabs.Trigger value={tabValue({ kind: "everyone" })}>Everyone</Tabs.Trigger>
      {#each groups as group (group.id)}
        {#if group.id}
          {#if group.sharesName}
            <Tabs.Trigger
              value={tabValue({ kind: "group", id: group.id })}
              aria-label="{group.name}, {group.teamName}"
            >
              <GroupName {group} />
            </Tabs.Trigger>
          {:else}
            <Tabs.Trigger value={tabValue({ kind: "group", id: group.id })}>{group.name}</Tabs.Trigger>
          {/if}
        {/if}
      {/each}
      {#if views.length > 0}
        <!-- Groups and views each start a row; the rule between them is not a tab. -->
        <div class="col-span-full mx-1 my-0.5 h-px bg-border" aria-hidden="true" data-testid="people-tabs-divider"></div>
      {/if}
      {#each views as summary (summary.view.id)}
        {@const view = summary.view}
        {#if view.id}
          {#if summary.groupGone || summary.teamGone}
            <Tabs.Trigger value={tabValue({ kind: "view", id: view.id })}>
              <span>{view.name}</span>
              <span class="text-muted-foreground" aria-hidden="true">·</span>
              <span class="text-muted-foreground">
                {#if summary.groupGone && summary.teamGone}
                  Group gone, no one through team
                {:else if summary.groupGone}
                  Group gone
                {:else}
                  No one through team
                {/if}
              </span>
            </Tabs.Trigger>
          {:else}
            <Tabs.Trigger value={tabValue({ kind: "view", id: view.id })}>{view.name}</Tabs.Trigger>
          {/if}
        {/if}
      {/each}
    </Tabs.List>
    {@render actions?.()}
  </div>
  <Tabs.Content value={tabValue(tab)} class="space-y-6">
    {@render children()}
  </Tabs.Content>
</Tabs.Root>
