<script lang="ts">
  import type { Snippet } from "svelte";
  import type { StaffGroup } from "@nocturne/api-client";
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import { tabOfValue, tabValue, type PeopleTab } from "./people-tab.js";

  interface Props {
    /** The viewer's groups, in the API's order. */
    groups: StaffGroup[];
    tab: PeopleTab;
    onChange: (tab: PeopleTab) => void;
    /** What the open tab lists. */
    children: Snippet;
  }

  const { groups, tab, onChange, children }: Props = $props();
</script>

<Tabs.Root value={tabValue(tab)} onValueChange={(value) => onChange(tabOfValue(value))}>
  <Tabs.List variant="wrap" aria-label="Lists of people">
    <Tabs.Trigger value={tabValue({ kind: "everyone" })}>Everyone</Tabs.Trigger>
    {#each groups as group (group.id)}
      {#if group.id}
        {#if group.sharesName}
          <Tabs.Trigger
            value={tabValue({ kind: "group", id: group.id })}
            aria-label="{group.name}, {group.teamName}"
          >
            <span>{group.name}</span>
            <span class="text-muted-foreground" aria-hidden="true">·</span>
            <span class="text-muted-foreground">{group.teamName}</span>
          </Tabs.Trigger>
        {:else}
          <Tabs.Trigger value={tabValue({ kind: "group", id: group.id })}>{group.name}</Tabs.Trigger>
        {/if}
      {/if}
    {/each}
  </Tabs.List>
  <Tabs.Content value={tabValue(tab)} class="space-y-6">
    {@render children()}
  </Tabs.Content>
</Tabs.Root>
