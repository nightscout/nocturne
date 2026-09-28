<script lang="ts">
    import Brackets from "@lucide/svelte/icons/brackets";
    import Plus from "@lucide/svelte/icons/plus";
    import X from "@lucide/svelte/icons/x";
    import { Button } from "@nocturne/ui/ui/button";
    import { Checkbox } from "@nocturne/ui/ui/checkbox";
    import { Input } from "@nocturne/ui/ui/input";
    import * as Select from "@nocturne/ui/ui/select";
    import * as ToggleGroup from "@nocturne/ui/ui/toggle-group";
    import {
        convertFromDisplayUnits,
        convertToDisplayUnits,
        getUnitLabel,
        MGDL_PER_MMOL,
        type GlucoseUnits,
    } from "@nocturne/ui/glucose";
    import type { CompareOperator, GroupNode } from "./wire";
    import { defaultLeaf, fromRow, LEAF_KINDS, OPERATORS, toRow, type Row } from "./tree";
    import ConditionEditor from "./ConditionEditor.svelte";

    interface Props {
        group: GroupNode;
        units: GlucoseUnits;
        nested?: boolean;
        onremove?: () => void;
    }

    let { group, units, nested = false, onremove }: Props = $props();

    const conditions = $derived(group.composite.conditions);

    function setRow(index: number, next: Partial<Row>): void {
        const row = toRow(conditions[index]);
        if (row) conditions[index] = fromRow({ ...row, ...next });
    }

    function numberFrom(event: Event & { currentTarget: HTMLInputElement }, fallback: number): number {
        const n = Number(event.currentTarget.value);
        return Number.isFinite(n) ? n : fallback;
    }

    const rateUnit = $derived(units === "mmol" ? "mmol/L a minute" : "mg/dL a minute");
</script>

{#snippet operator(value: CompareOperator, onchange: (next: CompareOperator) => void)}
    <Select.Root
        type="single"
        {value}
        onValueChange={(v: string) => {
            const next = OPERATORS.find((o) => o.operator === v)?.operator;
            if (next) onchange(next);
        }}
    >
        <Select.Trigger size="xs">{OPERATORS.find((o) => o.operator === value)?.label}</Select.Trigger>
        <Select.Content>
            {#each OPERATORS as o (o.operator)}
                <Select.Item value={o.operator} label={o.label} />
            {/each}
        </Select.Content>
    </Select.Root>
{/snippet}

<div class="space-y-2">
    <div class="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
        {#if nested}
            <Brackets class="size-3.5" />
            <span>Group: match</span>
        {:else}
            <span>Notify when</span>
        {/if}
        <ToggleGroup.Root
            type="single"
            value={group.composite.operator}
            onValueChange={(v: string) => {
                if (v === "and" || v === "or") group.composite.operator = v;
            }}
            variant="segmented"
            size="xs"
        >
            <ToggleGroup.Item value="and" aria-label="All conditions must hold">all of</ToggleGroup.Item>
            <ToggleGroup.Item value="or" aria-label="Any condition is enough">any of</ToggleGroup.Item>
        </ToggleGroup.Root>
        <span>these are true:</span>
        {#if onremove}
            <span class="flex-1"></span>
            <Button variant="ghost" size="icon-xs" aria-label="Remove group" onclick={onremove}>
                <X />
            </Button>
        {/if}
    </div>

    <div class="space-y-1.5 {nested ? 'border-l border-border/60 pl-3' : ''}">
        {#each conditions as child, index (index)}
            {@const row = toRow(child)}
            {@const eyebrow = index === 0 ? "IF" : group.composite.operator === "and" ? "AND" : "OR"}
            {#if child.type === "composite"}
                <div class="flex items-start gap-2 rounded-md border bg-background p-2">
                    <span class="w-9 shrink-0 pt-1 text-2xs font-semibold uppercase tracking-wider text-muted-foreground">{eyebrow}</span>
                    <div class="min-w-0 flex-1">
                        <ConditionEditor
                            group={child}
                            {units}
                            nested
                            onremove={() => conditions.splice(index, 1)}
                        />
                    </div>
                </div>
            {:else if row}
                <div class="flex flex-wrap items-center gap-2 rounded-md border bg-background px-2 py-1.5">
                    <span class="w-9 shrink-0 text-2xs font-semibold uppercase tracking-wider text-muted-foreground">{eyebrow}</span>
                    <Checkbox
                        checked={row.negated}
                        aria-label="NOT: the opposite of this condition"
                        onCheckedChange={(c: boolean) => setRow(index, { negated: c === true })}
                    />
                    <span class="text-2xs font-semibold uppercase text-muted-foreground">not</span>
                    <Select.Root
                        type="single"
                        value={row.leaf.type}
                        onValueChange={(v: string) => {
                            const kind = LEAF_KINDS.find((k) => k.kind === v)?.kind;
                            if (kind) setRow(index, { leaf: defaultLeaf(kind) });
                        }}
                    >
                        <Select.Trigger size="xs">
                            {LEAF_KINDS.find((k) => k.kind === row.leaf.type)?.label}
                        </Select.Trigger>
                        <Select.Content>
                            {#each LEAF_KINDS as k (k.kind)}
                                <Select.Item value={k.kind} label={k.label} />
                            {/each}
                        </Select.Content>
                    </Select.Root>

                    {#if row.leaf.type === "threshold"}
                        {@const leaf = row.leaf}
                        <Select.Root
                            type="single"
                            value={leaf.threshold.direction}
                            onValueChange={(v: string) => {
                                if (v === "above" || v === "below") leaf.threshold.direction = v;
                            }}
                        >
                            <Select.Trigger size="xs">{leaf.threshold.direction}</Select.Trigger>
                            <Select.Content>
                                <Select.Item value="below" label="below" />
                                <Select.Item value="above" label="above" />
                            </Select.Content>
                        </Select.Root>
                        <Input
                            type="number"
                            size="xs"
                            step={units === "mmol" ? "0.1" : "1"}
                            class="w-20 text-right"
                            aria-label="Glucose value"
                            value={convertToDisplayUnits(leaf.threshold.value, units)}
                            oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                                leaf.threshold.value = convertFromDisplayUnits(
                                    numberFrom(e, convertToDisplayUnits(leaf.threshold.value, units)),
                                    units,
                                );
                            }}
                        />
                        <span class="text-xs text-muted-foreground">{getUnitLabel(units)}</span>
                    {:else if row.leaf.type === "rate_of_change"}
                        {@const leaf = row.leaf}
                        <Select.Root
                            type="single"
                            value={leaf.rate_of_change.direction}
                            onValueChange={(v: string) => {
                                if (v === "falling" || v === "rising") leaf.rate_of_change.direction = v;
                            }}
                        >
                            <Select.Trigger size="xs">{leaf.rate_of_change.direction}</Select.Trigger>
                            <Select.Content>
                                <Select.Item value="falling" label="falling" />
                                <Select.Item value="rising" label="rising" />
                            </Select.Content>
                        </Select.Root>
                        <span class="text-xs text-muted-foreground">at least</span>
                        <Input
                            type="number"
                            size="xs"
                            min="0"
                            step={units === "mmol" ? "0.01" : "0.1"}
                            class="w-20 text-right"
                            aria-label="Rate"
                            value={units === "mmol"
                                ? Math.round((leaf.rate_of_change.rate / MGDL_PER_MMOL) * 100) / 100
                                : leaf.rate_of_change.rate}
                            oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                                const n = numberFrom(e, NaN);
                                if (!Number.isFinite(n)) return;
                                leaf.rate_of_change.rate =
                                    units === "mmol" ? Math.round(n * MGDL_PER_MMOL * 10) / 10 : n;
                            }}
                        />
                        <span class="text-xs text-muted-foreground">{rateUnit}</span>
                    {:else if row.leaf.type === "signal_loss"}
                        {@const leaf = row.leaf}
                        <span class="text-xs text-muted-foreground">no reading for</span>
                        <Input
                            type="number"
                            size="xs"
                            min="1"
                            class="w-16 text-right"
                            aria-label="Minutes without a reading"
                            value={leaf.signal_loss.timeout_minutes}
                            oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                                leaf.signal_loss.timeout_minutes = Math.round(
                                    numberFrom(e, leaf.signal_loss.timeout_minutes),
                                );
                            }}
                        />
                        <span class="text-xs text-muted-foreground">min</span>
                    {:else if row.leaf.type === "time_of_day"}
                        {@const leaf = row.leaf}
                        <Input
                            type="time"
                            size="xs"
                            class="w-28"
                            aria-label="From"
                            value={leaf.time_of_day.from}
                            oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                                leaf.time_of_day.from = e.currentTarget.value;
                            }}
                        />
                        <span class="text-xs text-muted-foreground">to</span>
                        <Input
                            type="time"
                            size="xs"
                            class="w-28"
                            aria-label="To"
                            value={leaf.time_of_day.to}
                            oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                                leaf.time_of_day.to = e.currentTarget.value;
                            }}
                        />
                    {:else if row.leaf.type === "cob" || row.leaf.type === "iob"}
                        {@const payload = row.leaf.type === "cob" ? row.leaf.cob : row.leaf.iob}
                        {@render operator(payload.operator, (o) => (payload.operator = o))}
                        <Input
                            type="number"
                            size="xs"
                            min="0"
                            step={row.leaf.type === "cob" ? "1" : "0.1"}
                            class="w-20 text-right"
                            aria-label={row.leaf.type === "cob" ? "Grams" : "Units"}
                            value={payload.value}
                            oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                                payload.value = numberFrom(e, payload.value);
                            }}
                        />
                        <span class="text-xs text-muted-foreground">{row.leaf.type === "cob" ? "g" : "U"}</span>
                    {:else if row.leaf.type === "time_since_last_carb"}
                        {@const payload = row.leaf.time_since_last_carb}
                        {@render operator(payload.operator, (o) => (payload.operator = o))}
                        <Input
                            type="number"
                            size="xs"
                            min="0"
                            class="w-16 text-right"
                            aria-label="Minutes since carbs"
                            value={payload.minutes}
                            oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                                payload.minutes = Math.round(numberFrom(e, payload.minutes));
                            }}
                        />
                        <span class="text-xs text-muted-foreground">min ago</span>
                    {/if}

                    <span class="flex items-center gap-1.5">
                        <Checkbox
                            checked={row.minutes !== null}
                            aria-label="Only when true for a while"
                            onCheckedChange={(c: boolean) => setRow(index, { minutes: c === true ? 15 : null })}
                        />
                        <span class="text-xs text-muted-foreground">for at least</span>
                        {#if row.minutes !== null}
                            <Input
                                type="number"
                                size="xs"
                                min="1"
                                class="w-16 text-right"
                                aria-label="Minutes it must hold"
                                value={row.minutes}
                                oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                                    const n = Math.round(numberFrom(e, row.minutes ?? 15));
                                    setRow(index, { minutes: n });
                                }}
                            />
                            <span class="text-xs text-muted-foreground">min</span>
                        {/if}
                    </span>

                    <span class="flex-1"></span>
                    {#if conditions.length > 1}
                        <Button
                            variant="ghost"
                            size="icon-xs"
                            aria-label="Remove condition"
                            onclick={() => conditions.splice(index, 1)}
                        >
                            <X />
                        </Button>
                    {/if}
                </div>
            {/if}
        {/each}

        <div class="flex flex-wrap gap-1.5">
            <Button
                variant="dashed"
                size="xs"
                onclick={() => conditions.push(defaultLeaf("threshold"))}
            >
                <Plus /> Condition
            </Button>
            {#if !nested}
                <Button
                    variant="dashed"
                    size="xs"
                    onclick={() =>
                        conditions.push({
                            type: "composite",
                            composite: { operator: "and", conditions: [defaultLeaf("threshold"), defaultLeaf("rate_of_change")] },
                        })}
                >
                    <Brackets /> Group
                </Button>
            {/if}
        </div>
    </div>
</div>
