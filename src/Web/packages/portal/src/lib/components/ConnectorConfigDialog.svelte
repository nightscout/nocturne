<script lang="ts">
    import { Button } from "@nocturne/app/ui/button";
    import { Input } from "@nocturne/app/ui/input";
    import { Label } from "@nocturne/app/ui/label";
    import { Switch } from "@nocturne/app/ui/switch";
    import * as Dialog from "@nocturne/app/ui/dialog";
    import * as Select from "@nocturne/app/ui/select";
    import type { ConnectorMetadata } from "$lib/data/portal.remote";

    interface Props {
        open: boolean;
        connector: ConnectorMetadata | null;
        initialValues?: Record<string, string>;
        onOpenChange: (open: boolean) => void;
        onSave: (connector: ConnectorMetadata, values: Record<string, string>) => void;
    }

    let { open, connector, initialValues = {}, onOpenChange, onSave }: Props = $props();

    let configValues = $state<Record<string, string>>({});

    // Reset config values when dialog opens with new connector
    $effect(() => {
        if (open && connector) {
            const initial: Record<string, string> = { ...initialValues };

            // Apply defaults for fields that don't have values
            connector.fields.forEach((field) => {
                if (
                    initial[field.envVar] === undefined ||
                    initial[field.envVar] === ""
                ) {
                    if (field.default) {
                        initial[field.envVar] = field.default;
                    }
                }
            });
            configValues = initial;
        }
    });

    function handleSave() {
        if (connector) {
            onSave(connector, configValues);
            onOpenChange(false);
        }
    }
</script>

<Dialog.Root {open} {onOpenChange}>
    <Dialog.Content class="sm:max-w-[425px] max-h-[85vh] overflow-y-auto">
        {#if connector}
            <Dialog.Header>
                <Dialog.Title>Configure {connector.displayName}</Dialog.Title>
                <Dialog.Description>
                    {connector.description}
                </Dialog.Description>
            </Dialog.Header>

            <div class="grid gap-6 py-4">
                {#each connector.fields as field}
                    <div class="grid gap-2">
                        <Label for={field.envVar} class="text-sm font-medium">
                            {field.name}
                            {#if field.required}
                                <span class="text-destructive">*</span>
                            {/if}
                        </Label>

                        {#if field.type === "boolean"}
                            <div class="flex items-center space-x-2">
                                <Switch
                                    id={field.envVar}
                                    checked={configValues[field.envVar] === "true"}
                                    onCheckedChange={(v) =>
                                        (configValues[field.envVar] = v.toString())}
                                />
                                <Label
                                    for={field.envVar}
                                    class="font-normal text-muted-foreground"
                                >
                                    {field.description}
                                </Label>
                            </div>
                        {:else if field.type === "select" && field.options}
                            <Select.Root
                                type="single"
                                value={configValues[field.envVar]}
                                onValueChange={(v) => (configValues[field.envVar] = v)}
                            >
                                <Select.Trigger id={field.envVar}>
                                    {#if configValues[field.envVar]}
                                        {configValues[field.envVar]}
                                    {:else}
                                        <span class="text-muted-foreground">
                                            Select an option
                                        </span>
                                    {/if}
                                </Select.Trigger>
                                <Select.Content>
                                    {#each field.options as option}
                                        <Select.Item value={option} label={option} />
                                    {/each}
                                </Select.Content>
                            </Select.Root>
                            <p class="text-[0.8rem] text-muted-foreground">
                                {field.description}
                            </p>
                        {:else}
                            <Input
                                id={field.envVar}
                                type={field.type === "password"
                                    ? "password"
                                    : field.type === "number"
                                      ? "number"
                                      : "text"}
                                value={configValues[field.envVar] || ""}
                                oninput={(e) =>
                                    (configValues[field.envVar] = e.currentTarget.value)}
                                placeholder={field.default || ""}
                                required={field.required}
                            />
                            <p class="text-[0.8rem] text-muted-foreground">
                                {field.description}
                            </p>
                        {/if}
                    </div>
                {/each}

                {#if connector.fields.length === 0}
                    <p class="text-sm text-muted-foreground italic">
                        No configuration required for this connector.
                    </p>
                {/if}
            </div>

            <Dialog.Footer>
                <Button variant="outline" onclick={() => onOpenChange(false)}>
                    Cancel
                </Button>
                <Button onclick={handleSave}>Save Changes</Button>
            </Dialog.Footer>
        {:else}
            <div class="p-4 flex justify-center py-8">
                <div
                    class="animate-spin w-8 h-8 border-2 border-primary border-t-transparent rounded-full"
                ></div>
            </div>
        {/if}
    </Dialog.Content>
</Dialog.Root>
