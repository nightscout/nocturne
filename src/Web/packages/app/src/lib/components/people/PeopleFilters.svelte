<script lang="ts">
  import { getFlagRuleFacts } from "#lib/api/generated/flagRules.generated.remote.js";
  import { getMyTeams } from "#lib/api/generated/teams.generated.remote.js";
  import {
    PeopleFilterKind,
    type PeopleFilter,
    type StaffGroup,
    type TherapyType,
  } from "@nocturne/api-client";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import ListFilter from "@lucide/svelte/icons/list-filter";
  import FlagRuleForm from "./FlagRuleForm.svelte";
  import GroupName from "./GroupName.svelte";
  import { findFact } from "./flag-rule-form.js";
  import { THERAPY_TYPES, filterSentence, therapyLabel } from "./people-filter-text.svelte.js";
  import { MAX_FILTERS, factFilterOf } from "./people-query.js";

  interface Props {
    filters: PeopleFilter[];
    /** The viewer's groups, undefined until they load. */
    groups: StaffGroup[] | undefined;
    onChange: (filters: PeopleFilter[]) => void;
  }

  const { filters, groups, onChange }: Props = $props();

  type Choice =
    | "fact"
    | "age-at-least"
    | "age-at-most"
    | "therapy"
    | "flagged"
    | "direct"
    | "team"
    | "group";

  const CHOICES: Choice[] = [
    "fact",
    "age-at-least",
    "age-at-most",
    "therapy",
    "flagged",
    "direct",
    "team",
    "group",
  ];

  function choiceLabel(choice: Choice): string {
    switch (choice) {
      case "fact":
        return "A figure of their data";
      case "age-at-least":
        return "Age at least";
      case "age-at-most":
        return "Age at most";
      case "therapy":
        return "Therapy type";
      case "flagged":
        return "Flagged only";
      case "direct":
        return "Followed directly";
      case "team":
        return "Followed through a team";
      default:
        return "In a group";
    }
  }

  let adding = $state(false);
  let choice = $state<Choice | null>(null);
  let years = $state<number | null>(null);
  let therapyTypes = $state<TherapyType[]>([]);
  let teamId = $state("");
  let groupId = $state("");

  const atLimit = $derived(filters.length >= MAX_FILTERS);
  const needsFacts = $derived(
    choice === "fact" || filters.some((filter) => filter.kind === PeopleFilterKind.Fact)
  );
  const needsTeams = $derived(
    choice === "team" || filters.some((filter) => filter.kind === PeopleFilterKind.SourceTeam)
  );
  const factsQuery = $derived(needsFacts ? getFlagRuleFacts() : null);
  const teamsQuery = $derived(needsTeams ? getMyTeams() : null);
  const teams = $derived(teamsQuery?.current?.items ?? []);

  const factsFailed = $derived(!!factsQuery?.error && !factsQuery.current);

  function entryIn<T extends { id?: string }>(list: readonly T[] | undefined, id: string | undefined) {
    if (list === undefined) return undefined;
    return list.find((entry) => entry.id === id) ?? null;
  }

  function factOf(filter: PeopleFilter) {
    const facts = factsQuery?.current?.facts;
    if (!facts) return factsFailed ? null : undefined;
    return findFact(facts, filter.dataset, filter.field) ?? null;
  }

  function sentenceOf(filter: PeopleFilter): string {
    const team = entryIn(teamsQuery?.current?.items, filter.teamId);
    return filterSentence(filter, {
      fact: factOf(filter),
      teamName: team === undefined ? undefined : (team?.name ?? null),
      group: entryIn(groups, filter.groupId),
    });
  }

  function groupOfFilter(filter: PeopleFilter) {
    return filter.kind === PeopleFilterKind.InGroup ? entryIn(groups, filter.groupId) : undefined;
  }

  function open() {
    adding = true;
    choice = null;
    years = null;
    therapyTypes = [];
    teamId = "";
    groupId = "";
  }

  function close() {
    adding = false;
    choice = null;
  }

  function add(filter: PeopleFilter) {
    onChange([...filters, filter]);
    close();
  }

  function remove(index: number) {
    onChange(filters.toSpliced(index, 1));
  }

  const yearsValid = $derived(years != null && Number.isInteger(years) && years >= 0 && years <= 120);

  function filterOfChoice(): PeopleFilter | null {
    switch (choice) {
      case "age-at-least":
        return yearsValid && years != null ? { kind: PeopleFilterKind.AgeAtLeast, years } : null;
      case "age-at-most":
        return yearsValid && years != null ? { kind: PeopleFilterKind.AgeAtMost, years } : null;
      case "therapy":
        return therapyTypes.length > 0 ? { kind: PeopleFilterKind.Therapy, therapyTypes } : null;
      case "flagged":
        return { kind: PeopleFilterKind.Flagged };
      case "direct":
        return { kind: PeopleFilterKind.SourceDirect };
      case "team":
        return teamId ? { kind: PeopleFilterKind.SourceTeam, teamId } : null;
      case "group":
        return groupId ? { kind: PeopleFilterKind.InGroup, groupId } : null;
      default:
        return null;
    }
  }

  const chosen = $derived(filterOfChoice());

  function submit(event: SubmitEvent) {
    event.preventDefault();
    if (chosen) add(chosen);
  }

  function toggleTherapy(type: TherapyType, checked: boolean) {
    therapyTypes = checked
      ? THERAPY_TYPES.filter((each) => each === type || therapyTypes.includes(each))
      : therapyTypes.filter((each) => each !== type);
  }
</script>

<div class="space-y-3" data-testid="people-filters">
  {#if filters.length > 0}
    <ul class="flex flex-wrap gap-2" aria-label="Filters">
      {#each filters as filter, index (index)}
        {@const sentence = sentenceOf(filter)}
        {@const group = groupOfFilter(filter)}
        <li>
          <Badge
            variant="secondary"
            data-testid="people-filter"
            onremove={() => remove(index)}
            removeLabel="Remove the filter: {sentence}"
          >
            {#if group}
              In the group <GroupName {group} />
            {:else}
              {sentence}
            {/if}
          </Badge>
        </li>
      {/each}
    </ul>
  {/if}
  {#if factsFailed && filters.some((filter) => filter.kind === PeopleFilterKind.Fact)}
    <div class="flex flex-wrap items-center gap-2" data-testid="people-filter-facts-failed">
      <p class="text-sm text-muted-foreground">The list of figures did not load. The data filters show no values.</p>
      <Button variant="outline" size="sm" onclick={() => factsQuery?.refresh()}>Try again</Button>
    </div>
  {/if}

  {#if adding}
    <div class="space-y-3 rounded-lg border p-3" data-testid="people-filter-editor">
      <p class="text-sm text-muted-foreground">
        Filters combine with AND: the list shows a person only if the person matches all the filters.
      </p>
      <div class="space-y-1">
        <Label for="people-filter-kind">Filter by</Label>
        <Select.Root
          type="single"
          value={choice ?? ""}
          onValueChange={(value) => (choice = CHOICES.find((each) => each === value) ?? null)}
        >
          <Select.Trigger id="people-filter-kind" class="w-64" data-testid="people-filter-kind">
            {choice ? choiceLabel(choice) : "Choose a filter"}
          </Select.Trigger>
          <Select.Content>
            {#each CHOICES as each (each)}
              <Select.Item value={each} label={choiceLabel(each)} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>

      {#if choice === "fact"}
        {#if factsQuery?.error}
          <p class="text-sm text-muted-foreground">The list of figures did not load.</p>
          <Button variant="outline" size="sm" onclick={() => factsQuery?.refresh()}>Try again</Button>
        {:else if !factsQuery?.current}
          <p class="text-sm text-muted-foreground">Loading…</p>
        {:else}
          <FlagRuleForm
            mode="filter"
            facts={factsQuery.current.facts ?? []}
            presets={factsQuery.current.presets ?? []}
            initial={null}
            busy={false}
            onSave={(request) => add(factFilterOf(request))}
            onCancel={close}
          />
        {/if}
      {:else}
        <form class="space-y-3" onsubmit={submit}>
          {#if choice === "age-at-least" || choice === "age-at-most"}
            <div class="space-y-1">
              <Label for="people-filter-years">Age in years</Label>
              <Input
                id="people-filter-years"
                type="number"
                min={0}
                max={120}
                step={1}
                class="w-24"
                bind:value={years}
              />
            </div>
          {:else if choice === "therapy"}
            <fieldset class="space-y-2">
              <legend class="text-sm font-medium">Therapy type, any of</legend>
              {#each THERAPY_TYPES as type (type)}
                <div class="flex items-center gap-2">
                  <Checkbox
                    id="people-filter-therapy-{type}"
                    checked={therapyTypes.includes(type)}
                    onCheckedChange={(checked) => toggleTherapy(type, checked === true)}
                  />
                  <Label for="people-filter-therapy-{type}">{therapyLabel(type)}</Label>
                </div>
              {/each}
            </fieldset>
          {:else if choice === "team"}
            {#if teamsQuery?.error}
              <p class="text-sm text-muted-foreground">Your teams did not load.</p>
            {:else if !teamsQuery?.current}
              <p class="text-sm text-muted-foreground">Loading…</p>
            {:else if teams.length === 0}
              <p class="text-sm text-muted-foreground">You are not in a team.</p>
            {:else}
              <div class="space-y-1">
                <Label for="people-filter-team">Team</Label>
                <Select.Root type="single" bind:value={teamId}>
                  <Select.Trigger id="people-filter-team" class="w-64">
                    {teams.find((team) => team.id === teamId)?.name ?? "Choose a team"}
                  </Select.Trigger>
                  <Select.Content>
                    {#each teams as team (team.id)}
                      <Select.Item value={team.id ?? ""} label={team.name ?? ""} />
                    {/each}
                  </Select.Content>
                </Select.Root>
              </div>
            {/if}
          {:else if choice === "group"}
            {#if !groups || groups.length === 0}
              <p class="text-sm text-muted-foreground">You are not in a group.</p>
            {:else}
              <div class="space-y-1">
                <Label for="people-filter-group">Group</Label>
                <Select.Root type="single" bind:value={groupId}>
                  {@const picked = groups.find((group) => group.id === groupId)}
                  <Select.Trigger id="people-filter-group" class="w-64">
                    {#if picked}
                      <span><GroupName group={picked} /></span>
                    {:else}
                      Choose a group
                    {/if}
                  </Select.Trigger>
                  <Select.Content>
                    {#each groups as group (group.id)}
                      <Select.Item value={group.id ?? ""} label={group.name ?? ""}>
                        <span><GroupName {group} /></span>
                      </Select.Item>
                    {/each}
                  </Select.Content>
                </Select.Root>
              </div>
            {/if}
          {/if}
          <div class="flex gap-2">
            {#if choice}
              <Button type="submit" size="sm" disabled={!chosen}>Add filter</Button>
            {/if}
            <Button type="button" variant="ghost" size="sm" onclick={close}>Cancel</Button>
          </div>
        </form>
      {/if}
    </div>
  {:else}
    <div class="flex flex-wrap items-center gap-2">
      <Button variant="outline" size="sm" disabled={atLimit} onclick={open}>
        <ListFilter class="size-4" aria-hidden="true" />
        Add a filter
      </Button>
      {#if atLimit}
        <p class="text-sm text-muted-foreground" data-testid="people-filters-limit">
          You have {MAX_FILTERS} filters, the maximum. Remove a filter to add a new filter.
        </p>
      {/if}
    </div>
  {/if}
</div>
