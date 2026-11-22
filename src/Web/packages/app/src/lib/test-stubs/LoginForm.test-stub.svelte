<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";

  // Stands in for the sign-in form, whose passkey and provider ceremonies a browser test cannot
  // complete: one button finishes the sign-in as the real form does when it succeeds.
  let { returnUrl, onSuccess }: { returnUrl?: string; onSuccess?: () => void } = $props();
</script>

<p data-testid="login-form-return-url">{returnUrl}</p>
<Button onclick={() => onSuccess?.()}>Finish sign-in</Button>
