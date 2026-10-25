<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import HelpCircle from "@lucide/svelte/icons/circle-question-mark";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import KeyRound from "@lucide/svelte/icons/key-round";
  import Globe from "@lucide/svelte/icons/globe";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import { Artwork } from "@nocturne/watercolour";
  import { fingerprintArtwork } from "$lib/watercolour-icons";
  // Quote the sign-in controls by name — this is the page a locked-out person
  // reaches, so the names have to match what they're looking at.
  import { signInMethodLabels } from "$lib/components/auth/labels";
</script>

<svelte:head>
  <title>Sign-in help - Nocturne</title>
</svelte:head>

<div class="flex min-h-screen items-center justify-center bg-background p-4">
  <div class="w-full max-w-xl space-y-4">
    <Card.Root>
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
        >
          <HelpCircle class="h-6 w-6 text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">Sign-in help</Card.Title>
        <Card.Description>
          Learn about the different ways to sign in to Nocturne
        </Card.Description>
      </Card.Header>
    </Card.Root>

    <Card.Root>
      <Card.Header>
        <div class="flex items-center gap-3">
          <Artwork
            icon={fingerprintArtwork}
            palette="water"
            motion="auto"
            autoplay="once"
            class="size-56"
          />
          <Card.Title class="text-lg">Passkeys</Card.Title>
        </div>
      </Card.Header>
      <Card.Content>
        <p class="text-sm leading-relaxed text-muted-foreground">
          Passkeys are the primary way to sign in to Nocturne. They use the
          built-in security of your device, such as a fingerprint sensor, face
          recognition, or a hardware security key. There is no password to
          remember.
        </p>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          On the sign-in page, select
          <strong>{signInMethodLabels.passkey}</strong> and follow the prompt on
          your device. If you have multiple accounts, choose
          <strong>{signInMethodLabels.username}</strong> and enter your username
          first.
        </p>
      </Card.Content>
    </Card.Root>

    <Card.Root>
      <Card.Header>
        <div class="flex items-center gap-3">
          <div
            class="flex h-9 w-9 items-center justify-center rounded-full bg-primary/10"
          >
            <Smartphone class="h-5 w-5 text-primary" />
          </div>
          <Card.Title class="text-lg">Authenticator app</Card.Title>
        </div>
      </Card.Header>
      <Card.Content>
        <p class="text-sm leading-relaxed text-muted-foreground">
          An authenticator app (such as Google Authenticator or Authy) is a
          second step, not a way to sign in on its own. It shows a 6-digit code
          that refreshes every 30 seconds, and each code can be used once.
        </p>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          If you configure one, enter the code from the app on the sign-in page, straight after your passkey. You can configure an authenticator in your account settings after you sign in.
        </p>
      </Card.Content>
    </Card.Root>

    <Card.Root>
      <Card.Header>
        <div class="flex items-center gap-3">
          <div
            class="flex h-9 w-9 items-center justify-center rounded-full bg-primary/10"
          >
            <KeyRound class="h-5 w-5 text-primary" />
          </div>
          <Card.Title class="text-lg">Recovery codes</Card.Title>
        </div>
      </Card.Header>
      <Card.Content>
        <p class="text-sm leading-relaxed text-muted-foreground">
          Recovery codes are one-time backup codes you received when you first created your account. Use them if you cannot access your passkey or authenticator app.
        </p>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          On the sign-in page, choose
          <strong>{signInMethodLabels.recoveryCode}</strong>, then enter your
          username and one of your remaining codes. Each code works only once, so
          cross it off your list after use.
        </p>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          A code does not sign you in on its own. It gives you ten minutes to create a new passkey, and you sign in with that.
        </p>
      </Card.Content>
    </Card.Root>

    <Card.Root>
      <Card.Header>
        <div class="flex items-center gap-3">
          <div
            class="flex h-9 w-9 items-center justify-center rounded-full bg-primary/10"
          >
            <Globe class="h-5 w-5 text-primary" />
          </div>
          <Card.Title class="text-lg">External providers</Card.Title>
        </div>
      </Card.Header>
      <Card.Content>
        <p class="text-sm leading-relaxed text-muted-foreground">
          Your Nocturne instance can allow you to sign in with an external
          account, such as Google, GitHub, or Microsoft. If it does, you will see
          provider buttons on the sign-in page.
        </p>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          Your instance administrator configures external providers. If you do
          not see any provider buttons on the sign-in page, this sign-in method is
          not enabled for your instance.
        </p>
      </Card.Content>
    </Card.Root>

    <div class="flex justify-center">
      <Button href="/auth/login" variant="outline">
        <ArrowLeft class="mr-2 h-4 w-4" />
        Back to sign-in
      </Button>
    </div>
  </div>
</div>
