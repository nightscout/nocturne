<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";

  // Stands in for the sign-in form, whose passkey and provider ceremonies a browser test cannot
  // complete: one button starts a sign-in and leaves it in flight, one ends it as a failure, and
  // one finishes it as the real form does when it succeeds. Like the real form, it reports not
  // busy when it unmounts.
  let {
    returnUrl,
    onSuccess,
    onBusyChange,
  }: {
    returnUrl?: string;
    onSuccess?: () => void;
    onBusyChange?: (busy: boolean) => void;
  } = $props();

  $effect(() => () => onBusyChange?.(false));
</script>

<p data-testid="login-form-return-url">{returnUrl}</p>
<Button onclick={() => onBusyChange?.(true)}>Start sign-in</Button>
<Button onclick={() => onBusyChange?.(false)}>Fail sign-in</Button>
<Button
  onclick={() => {
    onBusyChange?.(false);
    onSuccess?.();
  }}>Finish sign-in</Button
>
