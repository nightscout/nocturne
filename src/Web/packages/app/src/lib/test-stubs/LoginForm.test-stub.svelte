<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";

  // Stands in for the sign-in form, whose passkey and provider ceremonies a browser test cannot
  // complete: one button finishes the sign-in as the real form does when it succeeds, and one
  // puts it where the real form shows first recovery codes.
  let {
    returnUrl,
    onSuccess,
    onShowingRecoveryCodes,
  }: {
    returnUrl?: string;
    onSuccess?: () => void;
    onShowingRecoveryCodes?: (showing: boolean) => void;
  } = $props();
</script>

<p data-testid="login-form-return-url">{returnUrl}</p>
<Button onclick={() => onShowingRecoveryCodes?.(true)}>Show recovery codes</Button>
<Button
  onclick={() => {
    onShowingRecoveryCodes?.(false);
    onSuccess?.();
  }}>Finish sign-in</Button
>
