<script lang="ts">
    import { RadioGroup, RadioGroupCard } from "@nocturne/ui/ui/radio-group";
    import { Badge } from "@nocturne/ui/ui/badge";
    import { getInstallGuide } from "./guide.svelte.js";

    interface Option {
        value: string;
        title: string;
        description: string;
        recommended?: boolean;
    }

    interface Props {
        /** Names the choice in the guide. Read it back with `guide.choice(id, fallback)`. */
        id: string;
        /** The question, read to a screen reader as the group's name. */
        label: string;
        options: Option[];
        /** The route shown until the reader picks one. */
        fallback: string;
    }

    let { id, label, options, fallback }: Props = $props();

    const guide = getInstallGuide();

    // A stored choice that is not an option now would select no card and show no route.
    $effect(() => {
        if (guide && !options.some((option) => option.value === guide.choice(id, fallback))) {
            guide.choose(id, fallback);
        }
    });
</script>

{#if guide}
    <div class="not-prose mb-6">
    <RadioGroup
        value={guide.choice(id, fallback)}
        onValueChange={(value) => guide.choose(id, value)}
        aria-label={label}
        class="sm:grid-cols-2"
    >
        {#each options as option (option.value)}
            <RadioGroupCard value={option.value}>
                <span class="flex flex-wrap items-center gap-2 text-sm font-semibold text-foreground">
                    {option.title}
                    {#if option.recommended}
                        <Badge variant="secondary">Recommended</Badge>
                    {/if}
                </span>
                <span class="text-sm text-muted-foreground">{option.description}</span>
            </RadioGroupCard>
        {/each}
    </RadioGroup>
    </div>
{/if}
