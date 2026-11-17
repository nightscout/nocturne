<script lang="ts">
    import CopyButton from "../CopyButton.svelte";
    import { getInstallGuide } from "./guide.svelte.js";

    interface Field {
        /** The field's label in the form the reader fills in. */
        label: string;
        /** The variable in the keys file that holds its value. */
        name: string;
    }

    interface Props {
        fields: Field[];
        /** The id of the step that makes the file, for the link shown while the file is not ready. */
        keysStep: string;
    }

    let { fields, keysStep }: Props = $props();

    const guide = getInstallGuide();
</script>

{#if guide}
    <div class="not-prose my-3">
        <ul class="m-0 list-none divide-y divide-border/60 rounded-lg border border-border/60 p-0">
            {#each fields as field (field.name)}
                {@const value = guide.keysReady ? guide.values?.[field.name] : undefined}
                <li class="flex items-center gap-3 px-3 py-2">
                    <div class="min-w-0 flex-1">
                        <p class="m-0 text-sm font-medium text-foreground">{field.label}</p>
                        <p class="m-0 truncate font-mono text-xs text-muted-foreground">
                            {value ?? field.name}
                        </p>
                    </div>
                    {#if value}
                        <CopyButton text={value} label="Copy {field.label}" kind="password" />
                    {/if}
                </li>
            {/each}
        </ul>
        {#if !guide.keysReady}
            <p class="m-0 mt-2 text-sm text-muted-foreground">
                <a href="#{keysStep}" class="text-primary hover:underline">Download and save the file</a>
                first. Then this list shows each value with a copy button.
            </p>
        {/if}
    </div>
{/if}
