<!--
  The quiet options of a chart around an event or over a typical day. Around an event: the window as
  one chip, the options in use as removable chips, and one "Options" menu for the rest. For both: the
  time left out as removable chips (the API's defaults are already there) and one "Leave out" menu.
  A row only shows when it applies to a signal on the chart.
-->
<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import * as DropdownMenu from "#lib/components/ui/dropdown-menu/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Popover from "#lib/components/ui/popover/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import Plus from "@lucide/svelte/icons/plus";
  import X from "@lucide/svelte/icons/x";
  import DateSpanField from "./DateSpanField.svelte";
  import { LEAVE_OUTS, signal as signalOf } from "./vocabulary.js";
  import {
    minutesText,
    windowText,
    type Workspace,
  } from "./workspace.svelte.js";

  interface Props {
    ws: Workspace;
  }

  let { ws }: Props = $props();

  let comparisonOpen = $state(false);
  let profileOpen = $state(false);
  let profileText = $state("");

  const isEvent = $derived(ws.lens === "event");
  const left = $derived(LEAVE_OUTS.filter((l) => ws.leaveOut[l.id]));
  const notLeft = $derived(LEAVE_OUTS.filter((l) => !ws.leaveOut[l.id]));

  function saveProfile(event: SubmitEvent) {
    event.preventDefault();
    ws.setProfileName(profileText);
    profileOpen = false;
  }
</script>

{#snippet removable(label: string, remove: () => void, testid: string)}
  <span class="inline-flex items-center rounded-md border border-border" data-testid={testid}>
    <span class="px-2 text-xs">{label}</span>
    <Button variant="ghost" size="icon-xs" aria-label="Remove: {label}" onclick={remove}>
      <X aria-hidden="true" />
    </Button>
  </span>
{/snippet}

<div class="space-y-2" data-testid="chart-options">
  {#if isEvent}
    <div class="flex flex-wrap items-center gap-2">
      <span class="w-20 shrink-0 text-xs text-muted-foreground">Window</span>
      <Popover.Root>
        <Popover.Trigger>
          {#snippet child({ props }: { props: Record<string, unknown> })}
            <Button
              {...props}
              variant="outline"
              size="xs"
              data-testid="event-window"
              aria-label="Window: {windowText(ws.event.before, ws.event.after)}"
            >
              {windowText(ws.event.before, ws.event.after)}
            </Button>
          {/snippet}
        </Popover.Trigger>
        <Popover.Content align="start" class="w-auto max-w-md space-y-4">
          <div class="space-y-1.5">
            <p class="text-xs text-muted-foreground" id="window-before">
              Before each event
            </p>
            <ToggleGroup.Root
              type="single"
              variant="choice"
              size="xs"
              spacing={1}
              class="flex-wrap"
              aria-labelledby="window-before"
              bind:value={
                () => String(ws.event.before),
                (v: string) => v && ws.setEvent({ before: Number(v) })
              }
            >
              {#each ws.beforeChoices as minutes (minutes)}
                <ToggleGroup.Item value={String(minutes)}>
                  {minutes === 0 ? "None" : minutesText(minutes)}
                </ToggleGroup.Item>
              {/each}
            </ToggleGroup.Root>
          </div>
          <div class="space-y-1.5">
            <p class="text-xs text-muted-foreground" id="window-after">
              After each event
            </p>
            <ToggleGroup.Root
              type="single"
              variant="choice"
              size="xs"
              spacing={1}
              class="flex-wrap"
              aria-labelledby="window-after"
              bind:value={
                () => String(ws.event.after),
                (v: string) => v && ws.setEvent({ after: Number(v) })
              }
            >
              {#each ws.afterChoices as minutes (minutes)}
                <ToggleGroup.Item value={String(minutes)}>
                  {minutesText(minutes)}
                </ToggleGroup.Item>
              {/each}
            </ToggleGroup.Root>
          </div>
        </Popover.Content>
      </Popover.Root>
      {#if ws.event.isolatedOnly}
        {@render removable(
          "Only events with nothing else near them",
          () => ws.setEvent({ isolatedOnly: false }),
          "option-isolated"
        )}
      {/if}
      {#if ws.event.requireCorroboration && ws.hasRises}
        {@render removable(
          "Only rises the loop answered with extra insulin",
          () => ws.setEvent({ requireCorroboration: false }),
          "option-corroboration"
        )}
      {/if}
      {#if ws.event.comparison || comparisonOpen}
        <span class="inline-flex items-center gap-1">
          <DateSpanField
            value={ws.event.comparison}
            bind:open={comparisonOpen}
            label="Compared with"
            placeholder="Pick the earlier period"
            onchange={(span) => ws.setEvent({ comparison: span })}
          />
          <Button
            variant="ghost"
            size="icon-xs"
            aria-label="Remove the earlier period"
            onclick={() => {
              comparisonOpen = false;
              ws.setEvent({ comparison: null });
            }}
          >
            <X aria-hidden="true" />
          </Button>
        </span>
      {/if}
      {#if !ws.event.isolatedOnly || (ws.hasRises && !ws.event.requireCorroboration) || !(ws.event.comparison || comparisonOpen)}
        <DropdownMenu.Root>
          <DropdownMenu.Trigger>
            {#snippet child({ props }: { props: Record<string, unknown> })}
              <Button {...props} variant="ghost" size="xs" data-testid="event-options">
                <Plus aria-hidden="true" />
                Options
              </Button>
            {/snippet}
          </DropdownMenu.Trigger>
          <DropdownMenu.Content align="start">
            {#if !ws.event.isolatedOnly}
              <DropdownMenu.Item onSelect={() => ws.setEvent({ isolatedOnly: true })}>
                Only events with nothing else near them
              </DropdownMenu.Item>
            {/if}
            {#if !(ws.event.comparison || comparisonOpen)}
              <DropdownMenu.Item onSelect={() => (comparisonOpen = true)}>
                Compare with an earlier period
              </DropdownMenu.Item>
            {/if}
            {#if ws.hasRises && !ws.event.requireCorroboration}
              <DropdownMenu.Item
                onSelect={() => ws.setEvent({ requireCorroboration: true })}
              >
                Only rises the loop answered with extra insulin
              </DropdownMenu.Item>
            {/if}
          </DropdownMenu.Content>
        </DropdownMenu.Root>
      {/if}
    </div>
  {/if}

  {#if ws.leaveOutApplies}
    <div class="flex flex-wrap items-center gap-2">
      <span class="w-20 shrink-0 text-xs text-muted-foreground">Leave out</span>
      {#each left as l (l.id)}
        {@render removable(l.label, () => ws.setLeaveOut(l.id, false), `leave-out-${l.id}`)}
      {/each}
      {#if ws.profileName}
        {@render removable(
          `Time when ${ws.profileName} was not active`,
          () => ws.setProfileName(null),
          "leave-out-profile"
        )}
      {/if}
      {#if notLeft.length > 0 || !ws.profileName}
        <Popover.Root bind:open={profileOpen}>
          <DropdownMenu.Root>
            <DropdownMenu.Trigger>
              {#snippet child({ props }: { props: Record<string, unknown> })}
                <Button {...props} variant="ghost" size="xs" data-testid="leave-out-add">
                  <Plus aria-hidden="true" />
                  Leave out
                </Button>
              {/snippet}
            </DropdownMenu.Trigger>
            <DropdownMenu.Content align="start">
              {#each notLeft as l (l.id)}
                <DropdownMenu.Item onSelect={() => ws.setLeaveOut(l.id, true)}>
                  {l.label}
                </DropdownMenu.Item>
              {/each}
              {#if !ws.profileName}
                <DropdownMenu.Item
                  onSelect={() => {
                    profileText = "";
                    profileOpen = true;
                  }}
                >
                  Time when a profile was not active
                </DropdownMenu.Item>
              {/if}
            </DropdownMenu.Content>
          </DropdownMenu.Root>
          <Popover.Trigger>
            {#snippet child({ props }: { props: Record<string, unknown> })}
              <span {...props} class="sr-only"></span>
            {/snippet}
          </Popover.Trigger>
          <Popover.Content align="start" class="w-72">
            <form class="space-y-2" onsubmit={saveProfile}>
              <Label for="profile-name">Only while this profile is active</Label>
              <Input
                id="profile-name"
                size="sm"
                maxlength={100}
                placeholder="Profile name"
                bind:value={profileText}
              />
              <Button type="submit" size="sm" disabled={profileText.trim() === ""}>
                Use this profile
              </Button>
            </form>
          </Popover.Content>
        </Popover.Root>
      {/if}
    </div>
    {#if ws.leaveOutMisses.length > 0}
      <p class="pl-22 text-xs text-muted-foreground">
        {ws.leaveOutMisses.map((id) => signalOf(id).label).join(", ")}: all time is
        shown. These signals cannot leave time out.
      </p>
    {/if}
  {/if}
</div>
