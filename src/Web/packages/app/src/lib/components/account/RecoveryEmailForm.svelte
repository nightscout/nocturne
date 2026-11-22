<script lang="ts">
  import { untrack } from "svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { FormError } from "#lib/forms/index.js";
  import { describeSubmitError, errorCode } from "#lib/forms/submit-error.js";
  import { setRecoveryEmail } from "#lib/api/generated/recoveryEmails.generated.remote.js";
  import { SAVED_WITHOUT_LINK, type RecoveryEmailOutcome } from "./recovery-email-outcome.js";

  interface Props {
    submitLabel: string;
    initialEmail?: string;
    /** Called once the address is stored, with what happened to the link. */
    onSaved: (outcome: RecoveryEmailOutcome) => void;
    /** Adds an equally prominent button that leaves without saving. */
    onSkip?: () => void;
    onCancel?: () => void;
  }

  let { submitLabel, initialEmail = "", onSaved, onSkip, onCancel }: Props = $props();

  let email = $state(untrack(() => initialEmail));
  let isSaving = $state(false);
  let error = $state<string | null>(null);

  async function save(event: SubmitEvent) {
    event.preventDefault();
    isSaving = true;
    error = null;
    try {
      const result = await setRecoveryEmail({ email: email.trim() });
      onSaved(result.sent ? "sent" : "unchanged");
    } catch (err) {
      const outcome = SAVED_WITHOUT_LINK[errorCode(err) ?? ""];
      if (outcome) {
        onSaved(outcome);
      } else {
        error = describeSubmitError(err, "We could not save your recovery email. Check the address and try again.", {
          email_links_unavailable: "This site cannot send email, so it cannot confirm a recovery email.",
        });
      }
    } finally {
      isSaving = false;
    }
  }
</script>

<form class="space-y-3" onsubmit={save}>
  <div class="space-y-1.5">
    <Label for="recovery-email-input">Recovery email</Label>
    <Input
      id="recovery-email-input"
      type="email"
      autocomplete="email"
      required
      maxlength={255}
      bind:value={email}
    />
  </div>
  <FormError issues={error} />
  <div class="flex flex-wrap gap-2">
    <Button type="submit" disabled={isSaving || email.trim() === ""}>
      {#if isSaving}
        <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
      {/if}
      {submitLabel}
    </Button>
    {#if onSkip}
      <Button type="button" variant="outline" disabled={isSaving} onclick={onSkip}>Skip for now</Button>
    {/if}
    {#if onCancel}
      <Button type="button" variant="outline" disabled={isSaving} onclick={onCancel}>Cancel</Button>
    {/if}
  </div>
</form>
