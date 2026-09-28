<script lang="ts">
    import { untrack } from "svelte";
    import BellOff from "@lucide/svelte/icons/bell-off";
    import Cpu from "@lucide/svelte/icons/cpu";
    import Plus from "@lucide/svelte/icons/plus";
    import RotateCcw from "@lucide/svelte/icons/rotate-ccw";
    import X from "@lucide/svelte/icons/x";
    import { Button } from "@nocturne/ui/ui/button";
    import * as Select from "@nocturne/ui/ui/select";
    import * as ToggleGroup from "@nocturne/ui/ui/toggle-group";
    import { glucoseUnits } from "@nocturne/app/stores/appearance-store.svelte";
    import ReplayView from "@nocturne/app/components/alerts/ReplayView.svelte";
    import { createStaticChartEngine } from "@nocturne/app/components/dashboard/glucose-chart/engine/chart-data-view.svelte.ts";
    import type { AlertReplayResult } from "@nocturne/app/api-client";
    import type { TransformedChartData } from "@nocturne/app/utils/chart-data-transform";
    import { replay, validate } from "./engine";
    import type { GroupNode, SimRule, ValidationIssue } from "./wire";
    import { SCENARIOS, ticksFor, type ScenarioId } from "./fixtures";
    import { PRESETS, presetRule, type PresetId } from "./presets";
    import { chartData, replayResult, ruleResponses } from "./replay-view";
    import { asGroup, collapse } from "./tree";
    import ConditionEditor from "./ConditionEditor.svelte";

    interface Props {
        /** The trace shown first. */
        scenario: ScenarioId;
        /** Offered as a picker when there is more than one; defaults to just `scenario`. */
        scenarios?: ScenarioId[];
        /** Replayed side by side over the same trace, so a reader can compare them. */
        rules: PresetId[];
        /** The rules can be edited, and example rules added and removed. */
        editable?: boolean;
    }

    let { scenario, scenarios, rules, editable = false }: Props = $props();

    /** The editor edits a group, as the app's does; `collapse` turns it back into the saved shape. */
    type EditingRule = SimRule & { condition: GroupNode };

    function editingRule(id: PresetId, index: number): EditingRule {
        const rule = presetRule(id, index);
        return { ...rule, condition: asGroup(rule.condition) };
    }

    const initialRules = () => rules.map(editingRule);

    let scenarioId = $state(untrack(() => scenario));
    let editing = $state(untrack(initialRules));
    let nextIndex = untrack(() => rules.length);
    /** Set together, so the chart never draws one trace under another trace's replay. */
    let view = $state.raw<{ result: AlertReplayResult; chart: TransformedChartData; rules: SimRule[] }>();
    let issues = $state<ValidationIssue[][]>([]);
    let failure = $state<string>();

    const units = $derived(glucoseUnits.current);
    const active = $derived(SCENARIOS[scenarioId]);
    const offered = $derived((scenarios ?? [scenario]).map((id) => SCENARIOS[id]));

    $effect(() => {
        const sent = $state.snapshot(editing).map((r) => ({ ...r, condition: collapse(r.condition) }));
        const trace = active;
        let cancelled = false;
        const timer = setTimeout(async () => {
            try {
                const found = await Promise.all(sent.map(validate));
                if (cancelled) return;
                issues = found;
                failure = undefined;
                if (found.some((f) => f.length > 0)) return;
                const ticks = ticksFor(trace);
                const replayed = await replay(sent, ticks);
                if (cancelled) return;
                view = { result: replayResult(trace, sent, ticks, replayed), chart: chartData(trace), rules: sent };
            } catch (error) {
                if (!cancelled) failure = error instanceof Error ? error.message : String(error);
            }
        }, 120);
        return () => {
            cancelled = true;
            clearTimeout(timer);
        };
    });

    const responses = $derived(view ? ruleResponses(view.rules) : []);

    function isPreset(id: string): id is PresetId {
        return id in PRESETS;
    }

    function addRule(id: string): void {
        if (isPreset(id)) editing.push(editingRule(id, nextIndex++));
    }

    function reset(): void {
        editing = initialRules();
        nextIndex = rules.length;
    }

    const issueText: Record<string, string> = {
        minutes_not_positive: "a time of at least 1 minute",
        empty_window: "a time window that opens: from and to cannot be the same",
        invalid_time: "times written as HH:mm",
        conditions_empty: "at least one condition in every group",
    };
</script>

<figure class="not-prose my-6 overflow-hidden rounded-lg border bg-card text-card-foreground" data-testid="alert-simulator">
    <div class="flex flex-wrap items-center gap-2 border-b px-3 py-2">
        {#if offered.length > 1}
            <ToggleGroup.Root
                type="single"
                value={scenarioId}
                onValueChange={(v: string) => {
                    const next = offered.find((s) => s.id === v);
                    if (next) scenarioId = next.id;
                }}
                variant="segmented"
                size="xs"
                class="flex-wrap"
            >
                {#each offered as s (s.id)}
                    <ToggleGroup.Item value={s.id}>{s.label}</ToggleGroup.Item>
                {/each}
            </ToggleGroup.Root>
        {:else}
            <span class="text-sm font-medium">{active.label}</span>
        {/if}
        <span class="flex-1"></span>
        <ToggleGroup.Root
            type="single"
            value={units}
            onValueChange={(v: string) => {
                if (v === "mg/dl" || v === "mmol") glucoseUnits.current = v;
            }}
            variant="segmented"
            size="xs"
        >
            <ToggleGroup.Item value="mg/dl">mg/dL</ToggleGroup.Item>
            <ToggleGroup.Item value="mmol">mmol/L</ToggleGroup.Item>
        </ToggleGroup.Root>
    </div>

    <div class="@container space-y-3 px-3 py-3">
        <p class="text-sm text-muted-foreground">{active.description}</p>
        {#if view}
            {@const current = view}
            <ReplayView
                result={current.result}
                rules={responses}
                seriesFromRules
                fixedRules
                chartEngine={({ range, onDataReady }) =>
                    createStaticChartEngine({ data: current.chart, range, onDataReady })}
            />
        {:else if !failure}
            <div class="h-[280px] animate-pulse rounded-md border bg-muted/30"></div>
        {/if}
    </div>

    {#if editable}
        <div class="space-y-3 border-t px-3 py-3">
            {#each editing as rule, index (rule.id)}
                <div class="space-y-1.5">
                    <div class="flex items-center gap-2">
                        <span class="text-sm font-medium">{rule.name}</span>
                        <span class="flex-1"></span>
                        {#if editing.length > 1}
                            <Button
                                variant="ghost"
                                size="icon-xs"
                                aria-label="Remove {rule.name}"
                                onclick={() => editing.splice(index, 1)}
                            >
                                <X />
                            </Button>
                        {/if}
                    </div>
                    <ConditionEditor group={rule.condition} {units} />
                    {#if issues[index]?.length}
                        <p class="text-sm text-destructive">
                            A save would be refused: this rule needs
                            {issues[index].map((i) => issueText[i.reason] ?? i.reason).join(", ")}.
                        </p>
                    {/if}
                </div>
            {/each}
            <div class="flex flex-wrap gap-2">
                <Select.Root type="single" value="" onValueChange={addRule}>
                    <Select.Trigger size="xs" class="w-auto">
                        <Plus /> Add an example rule
                    </Select.Trigger>
                    <Select.Content>
                        {#each Object.entries(PRESETS) as [id, preset] (id)}
                            <Select.Item value={id} label={preset.name} />
                        {/each}
                    </Select.Content>
                </Select.Root>
                <Button variant="outline" size="xs" onclick={reset}>
                    <RotateCcw /> Start over
                </Button>
            </div>
        </div>
    {/if}

    <figcaption class="flex items-start gap-2 border-t bg-muted/30 px-3 py-2 text-xs text-muted-foreground">
        {#if failure}
            <BellOff class="mt-0.5 size-3.5 shrink-0" />
            <span>The alert engine could not run here ({failure}). The rest of this page still applies.</span>
        {:else}
            <Cpu class="mt-0.5 size-3.5 shrink-0" />
            <span>
                Nocturne's own alert engine and Replay, running in your browser on made-up readings. Not
                anyone's data, and not medical advice.
            </span>
        {/if}
    </figcaption>
</figure>
