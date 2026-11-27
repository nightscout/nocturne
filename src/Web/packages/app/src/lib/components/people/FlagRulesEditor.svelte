<script lang="ts">
  import {
    createFlagRule,
    createTeamFlagRule,
    deleteFlagRule,
    deleteTeamFlagRule,
    getFlagRuleFacts,
    getFlagRules,
    getTeamFlagRules,
    updateFlagRule,
    updateTeamFlagRule,
  } from "#lib/api/generated/flagRules.generated.remote.js";
  import type { FlagRuleRequest, FlagRuleSummary } from "@nocturne/api-client";
  import { Button } from "#lib/components/ui/button/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import {
    VALUE_TONE,
    alertLevel,
    alertLevelPrefix,
  } from "#lib/components/status-pills/tracker-pill-style.svelte.js";
  import Info from "@lucide/svelte/icons/info";
  import Pencil from "@lucide/svelte/icons/pencil";
  import Plus from "@lucide/svelte/icons/plus";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import FlagRuleForm from "./FlagRuleForm.svelte";
  import { draftOfRule, findFact, type FlagRuleOwner } from "./flag-rule-form.js";
  import { ruleSentence } from "./flag-rule-text.svelte.js";

  interface Props {
    owner: FlagRuleOwner;
    /** Whether the viewer may add, change and delete the rules. A viewer who may not sees the list alone. */
    canEdit: boolean;
  }

  const { owner, canEdit }: Props = $props();
  const teamId = $derived(owner.kind === "team" ? owner.teamId : null);

  const rulesQuery = $derived(teamId ? getTeamFlagRules(teamId) : getFlagRules());
  const factsQuery = getFlagRuleFacts();
  const rules = $derived(rulesQuery.current?.rules ?? []);
  const limit = $derived(rulesQuery.current?.limit ?? 0);
  const facts = $derived(factsQuery.current?.facts ?? []);
  const presets = $derived(factsQuery.current?.presets ?? []);
  const atLimit = $derived(!!rulesQuery.current && rules.length >= limit);

  /** "new" while adding, a rule's id while changing it, else null. */
  let editing = $state<string | null>(null);
  let busy = $state(false);
  let error = $state<string | null>(null);

  async function run(failure: string, command: () => Promise<unknown>): Promise<boolean> {
    if (busy) return false;
    busy = true;
    error = null;
    try {
      await command();
      return true;
    } catch (err) {
      error = describeSubmitError(err, failure, {
        flag_rule_limit_reached: teamId
          ? "This team has the maximum number of flag rules. Delete a rule to add a new rule."
          : "You have the maximum number of flag rules. Delete a rule to add a new rule.",
      });
      return false;
    } finally {
      busy = false;
    }
  }

  async function add(request: FlagRuleRequest) {
    const create = teamId ? () => createTeamFlagRule({ teamId, request }) : () => createFlagRule(request);
    if (await run("Could not add the rule. Try again.", create)) editing = null;
  }

  async function change(ruleId: string, request: FlagRuleRequest) {
    const update = teamId
      ? () => updateTeamFlagRule({ teamId, ruleId, request })
      : () => updateFlagRule({ ruleId, request });
    if (await run("Could not change the rule. Try again.", update)) editing = null;
  }

  async function remove(ruleId: string) {
    const del = teamId ? () => deleteTeamFlagRule({ teamId, ruleId }) : () => deleteFlagRule(ruleId);
    await run("Could not delete the rule. Try again.", del);
  }

  function sentenceOf(summary: FlagRuleSummary): string {
    const rule = summary.rule;
    const fact = findFact(facts, rule.dataset, rule.field);
    return ruleSentence(rule, fact?.field.label ?? rule.field ?? "", fact?.field.unit);
  }
</script>

<div class="space-y-4">
  {#if rulesQuery.error || factsQuery.error}
    <div class="space-y-2">
      <p class="text-sm text-muted-foreground">
        {#if teamId}
          The flag rules of this team did not load.
        {:else}
          Your flag rules did not load.
        {/if}
      </p>
      <Button
        variant="outline"
        size="sm"
        onclick={() => {
          void rulesQuery.refresh();
          void factsQuery.refresh();
        }}
      >
        Try again
      </Button>
    </div>
  {:else if !rulesQuery.current || !factsQuery.current}
    <p class="text-sm text-muted-foreground">Loading…</p>
  {:else}
    {#if rules.length === 0 && editing !== "new"}
      <p class="text-sm text-muted-foreground" data-testid="flag-rules-empty">
        {#if teamId}
          This team has no flag rules.
        {:else}
          You have no flag rules.
        {/if}
      </p>
    {/if}
    <ul class="divide-y" data-testid="flag-rules">
      {#each rules as summary (summary.rule.id)}
        {@const rule = summary.rule}
        {@const id = rule.id ?? ""}
        {@const fact = findFact(facts, rule.dataset, rule.field)}
        {@const level = alertLevel(rule.level ?? null)}
        {@const prefix = alertLevelPrefix(level)}
        <li class="py-3" data-testid="flag-rule">
          {#if editing === id && fact}
            <FlagRuleForm
              {facts}
              presets={[]}
              initial={draftOfRule(rule, fact, glucoseUnits.current)}
              {busy}
              onSave={(request) => change(id, request)}
              onCancel={() => (editing = null)}
            />
          {:else}
            <div class="flex items-start gap-3">
              <span class="mt-0.5 {VALUE_TONE[level]}">
                {#if prefix}
                  <TriangleAlert class="size-4" aria-hidden="true" />
                  <span class="sr-only">{prefix}</span>
                {:else}
                  <Info class="size-4" aria-hidden="true" />
                {/if}
              </span>
              <div class="min-w-0 flex-1 space-y-0.5">
                <p class="text-sm font-medium" data-testid="flag-rule-sentence">{sentenceOf(summary)}</p>
                {#if summary.cannotCheckCount > 0}
                  <p class="text-xs text-muted-foreground" data-testid="flag-rule-cannot-check">
                    {#if teamId}
                      Cannot be checked for {summary.cannotCheckCount} of the people this team follows that you can see
                    {:else}
                      Cannot be checked for {summary.cannotCheckCount} of the people you follow
                    {/if}
                  </p>
                {/if}
              </div>
              {#if canEdit}
                <div class="flex shrink-0 gap-1">
                  <Button
                    variant="ghost"
                    size="icon"
                    aria-label="Edit rule"
                    disabled={busy || editing !== null || !fact}
                    onclick={() => {
                      error = null;
                      editing = id;
                    }}
                  >
                    <Pencil class="size-4" aria-hidden="true" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon"
                    aria-label="Delete rule"
                    disabled={busy || editing !== null}
                    onclick={() => remove(id)}
                  >
                    <Trash2 class="size-4" aria-hidden="true" />
                  </Button>
                </div>
              {/if}
            </div>
          {/if}
        </li>
      {/each}
    </ul>

    {#if canEdit && editing === "new"}
      <FlagRuleForm
        {facts}
        {presets}
        initial={null}
        {busy}
        onSave={add}
        onCancel={() => (editing = null)}
      />
    {:else if canEdit}
      <div class="space-y-2">
        <Button
          variant="outline"
          size="sm"
          disabled={atLimit || busy || editing !== null}
          onclick={() => {
            error = null;
            editing = "new";
          }}
        >
          <Plus class="size-4" aria-hidden="true" />
          Add a rule
        </Button>
        {#if atLimit}
          <p class="text-sm text-muted-foreground" data-testid="flag-rules-limit">
            {#if teamId}
              This team has {limit} rules, the maximum. Delete a rule to add a new rule.
            {:else}
              You have {limit} rules, the maximum. Delete a rule to add a new rule.
            {/if}
          </p>
        {/if}
      </div>
    {/if}

    {#if error}
      <p class="text-sm text-destructive" role="alert">{error}</p>
    {/if}
  {/if}
</div>
