<script lang="ts">
  import { page } from "$app/state";
  import { verifyEmailForm } from "../auth.remote";
  import { CheckCircle, XCircle, Loader2, Mail } from "lucide-svelte";

  // Get token from URL query parameter
  const token = $derived(page.url.searchParams.get("token") ?? "");
</script>

<svelte:head>
  <title>Verify Email | Nocturne</title>
</svelte:head>

<svelte:boundary>
  {#snippet failed(error, reset)}
    <div
      class="min-h-screen flex items-center justify-center bg-gray-50 dark:bg-gray-900 py-12 px-4 sm:px-6 lg:px-8"
    >
      <div class="max-w-md w-full space-y-8 text-center">
        <div
          class="mx-auto w-16 h-16 flex items-center justify-center rounded-full bg-red-100 dark:bg-red-900/30"
        >
          <XCircle class="w-10 h-10 text-red-600 dark:text-red-400" />
        </div>
        <h2
          class="mt-6 text-center text-3xl font-extrabold text-gray-900 dark:text-white"
        >
          Something went wrong
        </h2>
        <p class="text-gray-600 dark:text-gray-400">
          We couldn't verify your email. Please try again or contact support.
        </p>
        <p class="text-sm text-red-600 dark:text-red-400">
          {(error as Error).message}
        </p>
        <button
          onclick={reset}
          class="mt-4 text-indigo-600 hover:text-indigo-500 dark:text-indigo-400 font-medium"
        >
          Try again
        </button>
      </div>
    </div>
  {/snippet}

  <div
    class="min-h-screen flex items-center justify-center bg-gray-50 dark:bg-gray-900 py-12 px-4 sm:px-6 lg:px-8"
  >
    <div class="max-w-md w-full space-y-8">
      {#if !token}
        <!-- No token provided -->
        <div class="text-center">
          <div
            class="mx-auto w-16 h-16 flex items-center justify-center rounded-full bg-yellow-100 dark:bg-yellow-900/30"
          >
            <Mail class="w-10 h-10 text-yellow-600 dark:text-yellow-400" />
          </div>
          <h2
            class="mt-6 text-center text-3xl font-extrabold text-gray-900 dark:text-white"
          >
            Verify Your Email
          </h2>
          <p class="mt-4 text-gray-600 dark:text-gray-400">
            No verification token was provided. Please check your email for the
            verification link and click on it to verify your account.
          </p>
          <div class="mt-6">
            <a
              href="/auth/login"
              class="text-indigo-600 hover:text-indigo-500 dark:text-indigo-400 font-medium"
            >
              Return to login
            </a>
          </div>
        </div>
      {:else if verifyEmailForm.pending}
        <!-- Verifying -->
        <div class="text-center">
          <div
            class="mx-auto w-16 h-16 flex items-center justify-center rounded-full bg-indigo-100 dark:bg-indigo-900/30"
          >
            <Loader2
              class="w-10 h-10 text-indigo-600 dark:text-indigo-400 animate-spin"
            />
          </div>
          <h2
            class="mt-6 text-center text-3xl font-extrabold text-gray-900 dark:text-white"
          >
            Verifying Your Email
          </h2>
          <p class="mt-4 text-gray-600 dark:text-gray-400">
            Please wait while we verify your email address...
          </p>
        </div>
      {:else if verifyEmailForm.result?.success}
        <!-- Success -->
        <div class="text-center">
          <div
            class="mx-auto w-16 h-16 flex items-center justify-center rounded-full bg-green-100 dark:bg-green-900/30"
          >
            <CheckCircle class="w-10 h-10 text-green-600 dark:text-green-400" />
          </div>
          <h2
            class="mt-6 text-center text-3xl font-extrabold text-gray-900 dark:text-white"
          >
            Email Verified!
          </h2>
          <p class="mt-4 text-gray-600 dark:text-gray-400">
            {verifyEmailForm.result.message}
          </p>
          <div class="mt-6">
            <a
              href="/auth/login"
              class="inline-flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
            >
              Log In
            </a>
          </div>
        </div>
      {:else}
        <!-- Form state - show form for verification or errors -->
        <div class="text-center">
          <div
            class="mx-auto w-16 h-16 flex items-center justify-center rounded-full bg-indigo-100 dark:bg-indigo-900/30"
          >
            <Mail class="w-10 h-10 text-indigo-600 dark:text-indigo-400" />
          </div>
          <h2
            class="mt-6 text-center text-3xl font-extrabold text-gray-900 dark:text-white"
          >
            Verify Your Email
          </h2>

          {#each verifyEmailForm.fields.allIssues() ?? [] as issue}
            <p class="mt-4 text-red-600 dark:text-red-400">{issue.message}</p>
          {/each}

          {#if (verifyEmailForm.fields.allIssues() ?? []).length === 0}
            <p class="mt-4 text-gray-600 dark:text-gray-400">
              Click the button below to verify your email address.
            </p>
          {/if}

          <form {...verifyEmailForm} class="mt-6">
            <input type="hidden" name="token" value={token} />
            <button
              type="submit"
              class="inline-flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
              disabled={!!verifyEmailForm.pending}
            >
              {#if verifyEmailForm.pending}
                <Loader2 class="w-5 h-5 animate-spin mr-2" />
              {/if}
              Verify Email
            </button>
          </form>

          <div class="mt-6">
            <a
              href="/auth/login"
              class="text-indigo-600 hover:text-indigo-500 dark:text-indigo-400 font-medium"
            >
              Return to login
            </a>
          </div>
        </div>
      {/if}
    </div>
  </div>
</svelte:boundary>
