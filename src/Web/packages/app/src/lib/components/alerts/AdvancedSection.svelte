<script lang="ts">
  import { getSettingOptions } from "#lib/api/generated/alertRules.generated.remote.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import { freshnessIssue, type RuleEditorState } from "./types";

  interface Props {
    /** Edited in place. */
    editor: RuleEditorState;
  }

  let { editor = $bindable() }: Props = $props();

  const optionsQuery = getSettingOptions();
  const options = $derived(optionsQuery.current);
  const defaultBound = $derived(options?.defaultGlucoseFreshnessMinutes);
  const shownChoice = $derived(editor.onUnknown ?? options?.defaultOnUnknown);
  const choices = $derived(
    options?.onUnknownValues ?? (editor.onUnknown === undefined ? [] : [editor.onUnknown]),
  );
  const nonDefaultChoice = $derived(
    options !== undefined &&
      editor.onUnknown !== undefined &&
      editor.onUnknown !== options.defaultOnUnknown,
  );
  const rangeIssue = $derived(freshnessIssue(editor, options));
  const canUseDefault = $derived(
    (editor.hasStoredFreshnessBound && !editor.clearGlucoseFreshness) ||
      editor.glucoseFreshnessMinutes !== undefined,
  );
  const summary = $derived(
    [
      nonDefaultChoice && editor.onUnknown !== undefined ? choiceLabel(editor.onUnknown) : null,
      editor.glucoseFreshnessMinutes !== undefined ? `${editor.glucoseFreshnessMinutes} min` : null,
    ]
      .filter((part) => part !== null)
      .join(", "),
  );

  function choiceLabel(value: string): string {
    switch (value) {
      case "hold":
        return "Wait";
      case "met":
        return "Count the condition as true";
      case "not_met":
        return "Count the condition as false";
      default:
        return value;
    }
  }

  function choiceDescription(value: string): string | null {
    switch (value) {
      case "hold":
        return "The rule does not change the alert. It opens no alert, and it does not close an open alert, until it knows again. A true auto-resolve condition can still close the alert.";
      case "met":
        return "The rule counts its full condition as true. It can open an alert because data is missing. A “for N minutes” part does not delay this alert.";
      case "not_met":
        return "The rule counts its full condition as false. An open alert closes as it does when the condition stops being true. A condition that uses “not” also counts as false when data is missing.";
      default:
        return null;
    }
  }

  function setBound(raw: string): void {
    editor.glucoseFreshnessMinutes = raw === "" ? undefined : Number(raw);
    editor.clearGlucoseFreshness = raw === "" && editor.hasStoredFreshnessBound;
  }

  function useDefaultBound(): void {
    setBound("");
  }
</script>

<Collapsible.Root data-testid="alert-advanced">
  <Collapsible.Trigger class="group flex items-center gap-2">
    <ChevronRight
      class="size-4 shrink-0 text-muted-foreground transition-transform group-data-[state=open]:rotate-90"
      aria-hidden="true"
    />
    <span class="text-base font-semibold">Advanced</span>
    {#if summary}
      <span class="text-sm font-normal text-muted-foreground" data-testid="alert-advanced-summary">
        {summary}
      </span>
    {/if}
  </Collapsible.Trigger>
  <Collapsible.Content class="space-y-6 pt-4">
    <section class="space-y-3">
      <h3 id="alert-on-unknown-label" class="text-base font-semibold leading-none">
        When data is missing
      </h3>
      <p class="text-sm text-muted-foreground">
        These choices apply only when missing data stops the rule from knowing whether its full
        condition is true. If the other parts of the condition give a result, the rule uses that
        result.
      </p>
      {#if optionsQuery.error}
        <p class="text-sm text-destructive" role="alert" data-testid="alert-on-unknown-unavailable">
          The choices could not be loaded.
        </p>
      {/if}
      <RadioGroup.Root
        class="grid gap-2"
        aria-labelledby="alert-on-unknown-label"
        value={shownChoice}
        onValueChange={(value) => (editor.onUnknown = value)}
      >
        {#each choices as value (value)}
          {@const description = choiceDescription(value)}
          <RadioGroup.Card {value} data-testid="alert-on-unknown-{value}">
            <span class="text-sm font-medium">
              {choiceLabel(value)}{value === options?.defaultOnUnknown ? " (default)" : ""}
            </span>
            {#if description}
              <span class="text-sm text-muted-foreground">{description}</span>
            {/if}
          </RadioGroup.Card>
        {/each}
      </RadioGroup.Root>
      {#if nonDefaultChoice}
        <Alert.Root variant="warning" data-testid="alert-on-unknown-caution">
          <TriangleAlert class="size-4" aria-hidden="true" />
          <Alert.Description>
            If your data source sends readings less often than every 5 minutes, set a bound for
            this rule. Make it at least the time between two readings plus the time a reading can
            be late. If you do not: with “Count the condition as true”, the rule can open an alert
            between two readings that arrive on time. With “Count the condition as false”, it can
            close an open alert between them.
          </Alert.Description>
        </Alert.Root>
      {/if}
    </section>

    <section class="space-y-3">
      <div class="space-y-1.5">
        <Label for="alert-glucose-freshness">Minutes until a glucose reading counts as missing</Label>
        <p id="alert-glucose-freshness-help" class="text-sm text-muted-foreground">
          The rule counts a glucose reading as missing when Nocturne received it more than this number of
          minutes ago. This applies to conditions on the glucose value, rate of change, trend,
          glucose range and time windows. It does not apply to signal loss, data age, predicted
          glucose or Smart snooze conditions. In a time window, a gap in the readings that is
          longer than this can leave the condition unknown.
          {#if defaultBound !== undefined}
            The default is {defaultBound} minutes. Leave the field empty to use the default.
          {/if}
        </p>
      </div>
      <div class="flex flex-wrap items-center gap-2">
        <Input
          id="alert-glucose-freshness"
          type="number"
          class="max-w-32"
          min={options?.minGlucoseFreshnessMinutes}
          max={options?.maxGlucoseFreshnessMinutes}
          step="1"
          placeholder={defaultBound === undefined ? "" : String(defaultBound)}
          aria-describedby={rangeIssue
            ? "alert-glucose-freshness-help alert-glucose-freshness-error"
            : "alert-glucose-freshness-help"}
          aria-invalid={rangeIssue ? true : undefined}
          value={editor.glucoseFreshnessMinutes ?? ""}
          oninput={(e: Event & { currentTarget: HTMLInputElement }) =>
            setBound(e.currentTarget.value)}
        />
        <Button
          type="button"
          variant="outline"
          size="sm"
          onclick={useDefaultBound}
          disabled={!canUseDefault}
        >
          Use the default
        </Button>
      </div>
      {#if rangeIssue}
        <p id="alert-glucose-freshness-error" class="text-sm text-destructive" role="alert">
          {rangeIssue}
        </p>
      {/if}
    </section>
  </Collapsible.Content>
</Collapsible.Root>
