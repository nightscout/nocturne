<script lang="ts">
  import { resolve } from "$app/paths";
  import {
    createFlagRule,
    deleteFlagRule,
    getFlagRuleFacts,
    getFlagRules,
    updateFlagRule,
  } from "#lib/api/generated/flagRules.generated.remote.js";
  import type { FlagRuleRequest, FlagRuleSummary } from "@nocturne/api-client";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import {
    VALUE_TONE,
    alertLevel,
    alertLevelPrefix,
  } from "#lib/components/status-pills/tracker-pill-style.svelte.js";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import Flag from "@lucide/svelte/icons/flag";
  import Info from "@lucide/svelte/icons/info";
  import Pencil from "@lucide/svelte/icons/pencil";
  import Plus from "@lucide/svelte/icons/plus";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import FlagRuleForm from "./FlagRuleForm.svelte";
  import { draftOfRule, findFact } from "./flag-rule-form.js";
  import { ruleSentence } from "./flag-rule-text.svelte.js";

  const rulesQuery = getFlagRules();
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
        flag_rule_limit_reached: "You have the maximum number of flag rules. Delete a rule to add a new rule.",
      });
      return false;
    } finally {
      busy = false;
    }
  }

  async function add(request: FlagRuleRequest) {
    if (await run("Could not add the rule. Try again.", () => createFlagRule(request))) editing = null;
  }

  async function change(ruleId: string, request: FlagRuleRequest) {
    if (await run("Could not change the rule. Try again.", () => updateFlagRule({ ruleId, request })))
      editing = null;
  }

  async function remove(ruleId: string) {
    await run("Could not delete the rule. Try again.", () => deleteFlagRule(ruleId));
  }

  function sentenceOf(summary: FlagRuleSummary): string {
    const rule = summary.rule;
    const fact = findFact(facts, rule.dataset, rule.field);
    return ruleSentence(rule, fact?.field.label ?? rule.field ?? "", fact?.field.unit);
  }
</script>

<svelte:head>
  <title>Your flag rules - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-3xl space-y-6 p-4 md:p-6">
  <div class="space-y-2">
    <Button variant="ghost" size="sm" href={resolve("/(authenticated)/people")}>
      <ArrowLeft class="size-4" aria-hidden="true" />
      People
    </Button>
    <div class="flex items-center gap-3">
      <div class="flex size-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
        <Flag class="size-6 text-primary" aria-hidden="true" />
      </div>
      <div>
        <h1 class="text-2xl font-bold">Your flag rules</h1>
        <p class="text-sm text-muted-foreground">
          A person who meets one of your rules shows in Flagged on the People page. Only you see your rules.
        </p>
      </div>
    </div>
  </div>

  <Card.Root>
    <Card.Content class="space-y-4">
      {#if rulesQuery.error || factsQuery.error}
        <div class="space-y-2">
          <p class="text-sm text-muted-foreground">Your flag rules did not load.</p>
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
          <p class="text-sm text-muted-foreground" data-testid="flag-rules-empty">You have no flag rules.</p>
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
                        Cannot be checked for {summary.cannotCheckCount} of the people you follow
                      </p>
                    {/if}
                  </div>
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
                </div>
              {/if}
            </li>
          {/each}
        </ul>

        {#if editing === "new"}
          <FlagRuleForm
            {facts}
            {presets}
            initial={null}
            {busy}
            onSave={add}
            onCancel={() => (editing = null)}
          />
        {:else}
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
                You have {limit} rules, the maximum. Delete a rule to add a new rule.
              </p>
            {/if}
          </div>
        {/if}

        {#if error}
          <p class="text-sm text-destructive" role="alert">{error}</p>
        {/if}
      {/if}
    </Card.Content>
  </Card.Root>
</div>
