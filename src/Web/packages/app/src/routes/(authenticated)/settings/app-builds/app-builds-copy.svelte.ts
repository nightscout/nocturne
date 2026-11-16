import {
  AppBuildLinkState,
  AppBuildRepositoryProblem,
  AppBuildRunState,
} from "#lib/api/index.js";
import {
  describeRemoteError,
  errorMessage,
  errorStatus,
  type RemoteErrorPolicy,
} from "#lib/forms/submit-error.js";
import { recordApiFailure } from "#lib/support/api-failure-log.js";
import type {
  ImportProblem,
  NewPasswordProblem,
} from "#lib/app-builds/keystore.js";

// Every string here is behind a function: wuchale turns a translated string in a `.svelte.ts`
// module into derived state, which a module cannot export.

/**
 * The sentence for each `AppBuildException` code, or undefined for a code this
 * page does not know. The GitHub callback's `?error=` and a refused command
 * both carry these codes.
 */
export function errorText(code: string | null | undefined): string | undefined {
  switch (code) {
    case "not_configured":
      return "App builds are not configured on this Nocturne server.";
    case "unknown_target":
      return "Nocturne does not know this app.";
    case "not_linked":
      return "No repository is connected. Connect a repository first.";
    case "repository_not_chosen":
      return "Select a repository first.";
    case "repository_not_in_installation":
      return "You cannot use this repository. Select a repository that you are an admin of and that the Nocturne GitHub app can get access to.";
    case "repository_invalid":
      return "The repository did not pass the checks. It must be private and made from the template of the app.";
    case "repository_not_found":
      return "GitHub could not find the repository. Connect the repository again.";
    case "secret_not_allowed":
      return "Nocturne cannot write this secret to the repository.";
    case "input_invalid":
      return "Nocturne did not accept a value. Check the values and try again.";
    case "invalid_state":
      return "The connection attempt is not valid or it expired. Start again from this page.";
    case "installation_not_yours":
      return "Your GitHub account cannot get access to this installation. Connect again with the GitHub account that has the repository.";
    case "no_admin_repository":
      return "You are not an admin of a repository that the Nocturne GitHub app can get access to. Give the app access to your build repository, then connect again.";
    case "not_linker":
      return "Only the person who connected this repository can do this.";
    case "linker_not_admin":
      return "The person who connected this repository is not an admin of it now. Connect the repository again with an admin account.";
    case "signed_out":
      return "You were not signed in to Nocturne when GitHub sent you back. Sign in, then connect again.";
    case "forbidden":
      return "Your Nocturne account cannot use app builds now. Sign in again. If the problem continues, ask a site owner.";
    case "installation_gone":
      return "The Nocturne GitHub app is not installed on the repository now. Connect the repository again.";
    case "authorization_denied":
      return "GitHub did not give access. Connect again, and approve the request on GitHub.";
    case "github_refused":
      return "GitHub refused the request. Try again. If the problem continues, connect the repository again.";
    case "github_rejected":
      return "GitHub did not accept the request. Check the values and try again.";
    case "github_rate_limited":
      return "GitHub received too many requests. Wait some minutes, then try again.";
    case "github_unavailable":
      return "GitHub did not respond. Try again later.";
    default:
      return undefined;
  }
}

/** The banner for the GitHub callback's `?error=` code. */
export function callbackErrorText(code: string): string {
  return (
    errorText(code) ?? "Nocturne could not connect the repository. Try again."
  );
}

/**
 * The generated wrappers forward the code on 400, 403, 409 and 5xx, and only
 * the status on 404 and 429. A 429 can come from Nocturne's own limiter as well
 * as from GitHub, so the house wording answers it.
 */
const POLICY: RemoteErrorPolicy = {
  missing: () =>
    "Nocturne could not find the app build connection. Reload the page.",
  forbiddenReason: true,
  serverError: false,
  fault: () => "Nocturne could not complete the request. Try again.",
};

/**
 * The sentence for a rejected remote call. Each one is recorded for the support
 * form.
 */
export function describeError(err: unknown): string {
  const status = errorStatus(err);
  const known =
    status === 429
      ? undefined
      : (errorText(errorMessage(err)) ??
        (status === 403 ? errorText("forbidden") : undefined));
  if (known === undefined) {
    // Only the status goes on: this API's bodies are codes, so an unknown one is not a sentence.
    return describeRemoteError(
      { status },
      "Nocturne could not complete the request. Try again.",
      POLICY
    );
  }
  recordApiFailure(status, known);
  return known;
}

export function linkedText(): string {
  return "The repository is connected to Nocturne.";
}

export function targetTitle(targetId: string | undefined): string {
  switch (targetId) {
    case "aaps":
      return "AAPS";
    default:
      return targetId ?? "";
  }
}

export function targetDescription(
  targetId: string | undefined
): string | undefined {
  switch (targetId) {
    case "aaps":
      return "AndroidAPS, built by the AAPS Builder workflow in your GitHub repository.";
    default:
      return undefined;
  }
}

/** The name of the GitHub template the target's repository is made from. */
export function templateName(targetId: string | undefined): string {
  switch (targetId) {
    case "aaps":
      return "AAPS Builder";
    default:
      return "build";
  }
}

export function linkStateLabel(state: AppBuildLinkState | undefined): string {
  switch (state) {
    case AppBuildLinkState.RepositoryNotChosen:
      return "Select a repository";
    case AppBuildLinkState.Unreachable:
      return "GitHub not available";
    case AppBuildLinkState.Invalid:
      return "Repository not correct";
    case AppBuildLinkState.LinkerNotAdmin:
      return "Admin role missing";
    case AppBuildLinkState.SecretsMissing:
      return "Signing key missing";
    case AppBuildLinkState.Ready:
      return "Ready";
    default:
      return "Unknown";
  }
}

export function problemText(
  problem: AppBuildRepositoryProblem,
  targetId: string | undefined
): string {
  switch (problem) {
    case AppBuildRepositoryProblem.NotPrivate:
      return "The repository is public. Make it private on GitHub.";
    case AppBuildRepositoryProblem.IsTemplate:
      return "The repository is a template repository. Disable the template setting on GitHub.";
    case AppBuildRepositoryProblem.WrongTemplate:
      return `The repository was not made from the ${templateName(targetId)} template. Make a new repository from the template.`;
    default:
      return "The repository did not pass a check.";
  }
}

export function runStateLabel(state: AppBuildRunState | undefined): string {
  switch (state) {
    case AppBuildRunState.Queued:
      return "Queued";
    case AppBuildRunState.Running:
      return "Running";
    case AppBuildRunState.Succeeded:
      return "Succeeded";
    case AppBuildRunState.Failed:
      return "Failed";
    case AppBuildRunState.Cancelled:
      return "Canceled";
    default:
      return "Unknown";
  }
}

export function inputLabel(
  targetId: string | undefined,
  name: string | undefined
): string {
  if (targetId === "aaps" && name === "version") return "AAPS version";
  if (targetId === "aaps" && name === "variant") return "Variant";
  return name ?? "";
}

export function inputHelpText(
  targetId: string | undefined,
  name: string | undefined
): string | undefined {
  if (targetId === "aaps" && name === "version")
    return "Type latest, or the tag of an AAPS release.";
  if (targetId === "aaps" && name === "variant")
    return "Use fullRelease for the usual AAPS app.";
  return undefined;
}

export function newPasswordText(problem: NewPasswordProblem): string {
  switch (problem) {
    case "too_short":
      return "The password must have 8 or more characters.";
    case "mismatch":
      return "The passwords are not the same.";
    case "not_printable_ascii":
      return "Use only the letters a to z and A to Z, the digits 0 to 9, and symbols such as ! ? #. Do not use spaces, accented letters or the | character.";
  }
}

export function importProblemText(
  problem: ImportProblem,
  aliases?: string[]
): string {
  switch (problem) {
    case "not_keystore":
      return "This file is not a keystore file.";
    case "too_large":
      return "This file is too large to be a signing keystore.";
    case "empty":
      return "Type the keystore password, the alias and the key password.";
    case "contains_pipe":
      return "The passwords and the alias cannot contain the | character.";
    case "wrong_store_password":
      return "The keystore password is not correct.";
    case "alias_not_found":
      return aliases?.length
        ? `The keystore has no key with this alias. Aliases in the file: ${aliases.join(", ")}.`
        : "The keystore has no key with this alias.";
    case "wrong_key_password":
      return "The key password is not correct.";
  }
}

/**
 * After a refusal that a retry cannot fix, the sealed value is gone and the key
 * must be chosen again.
 */
export function chooseKeyAgainText(newKey: boolean): string {
  return newKey
    ? "Select Import my key, and import the backup file that you downloaded."
    : "Select Import my key, and choose the keystore file again.";
}
