import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushSync } from "svelte";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import sodium from "libsodium-wrappers";
import { page } from "$app/state";
import {
  AppBuildLinkState,
  AppBuildRepositoryProblem,
  AppBuildRunState,
  BuildInputKind,
  type AppBuildLinkStatus,
  type AppBuildRun,
  type AppBuildsStatus,
  type BuildTarget,
} from "#lib/api/index.js";
import { appBuildsMocks } from "#lib/test-stubs/app-builds.js";
import {
  createQueryResource,
  remoteQuery,
  remoteQueryError,
} from "#lib/test-stubs/remote-resource.js";
import { reactiveFields } from "#lib/test-stubs/reactive-fields.svelte.js";

import AppBuildsPage from "./app-builds-page.svelte";
import BuildRuns from "./BuildRuns.svelte";

vi.mock(
  "#lib/api/generated/appBuilds.generated.remote.js",
  () => import("#lib/test-stubs/app-builds.js")
);

const AAPS: BuildTarget = {
  id: "aaps",
  templateOwner: "nightscout",
  templateName: "aaps-builder",
  suggestedRepositoryName: "my-aaps",
  workflowFile: "build.yml",
  inputs: [
    {
      name: "version",
      kind: BuildInputKind.Text,
      default: "latest",
      options: [],
    },
    {
      name: "variant",
      kind: BuildInputKind.Choice,
      default: "fullRelease",
      options: ["fullRelease", "aapsclientRelease"],
    },
  ],
  secrets: [{ name: "KEYSTORE_SET", required: true }],
};

function link(
  state: AppBuildLinkState,
  overrides: Partial<AppBuildLinkStatus> = {}
): AppBuildLinkStatus {
  return {
    targetId: "aaps",
    state,
    accountLogin: "octo",
    repositoryFullName:
      state === AppBuildLinkState.RepositoryNotChosen
        ? undefined
        : "octo/my-aaps",
    repositoryUrl:
      state === AppBuildLinkState.RepositoryNotChosen
        ? undefined
        : "https://github.com/octo/my-aaps",
    linkedAt: "2026-11-01T10:00:00Z",
    linkedByCaller: true,
    ...overrides,
  };
}

function showStatus(status: AppBuildsStatus) {
  appBuildsMocks.status.mockReturnValue(remoteQuery(() => status));
}

const linked = (l: AppBuildLinkStatus) =>
  showStatus({ configured: true, targets: [AAPS], links: [l] });

function run(
  id: number,
  state: AppBuildRunState,
  title = `AAPS latest fullRelease ${id}`
): AppBuildRun {
  return {
    id,
    runNumber: id,
    title,
    state,
    createdAt: "2026-11-15T09:00:00Z",
    updatedAt: "2026-11-15T09:05:00Z",
    htmlUrl: `https://github.com/octo/my-aaps/actions/runs/${id}`,
  };
}

const button = (name: string) =>
  browserPage.getByRole("button", { name, exact: true });

/**
 * A refusal as SvelteKit hands a remote command's `error(status, code)` to the
 * browser.
 */
const refused = (status: number, code: string) => ({
  status,
  body: { message: code },
});

describe("App builds page", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    page.data.effectivePermissions = ["*"];
    appBuildsMocks.runs.mockReturnValue(remoteQuery(() => []));
    appBuildsMocks.repositories.mockReturnValue(remoteQuery(() => []));
  });

  afterEach(() => {
    delete page.data.effectivePermissions;
  });

  it("says the server has no GitHub app and links the operator docs, and nothing else", async () => {
    showStatus({ configured: false, targets: [AAPS], links: [] });
    render(AppBuildsPage);

    await expect
      .element(
        browserPage.getByText(
          "This Nocturne server has no GitHub app for app builds."
        )
      )
      .toBeVisible();
    await expect
      .element(
        browserPage.getByRole("link", {
          name: "How the server operator configures app builds",
        })
      )
      .toHaveAttribute(
        "href",
        expect.stringContaining("/docs/configuration/github-build-app")
      );
    await expect
      .element(button("Connect a repository"))
      .not.toBeInTheDocument();
  });

  it("explains who owns what and gives the two steps when nothing is linked", async () => {
    showStatus({ configured: true, targets: [AAPS], links: [] });
    appBuildsMocks.begin.mockResolvedValue({
      createRepositoryUrl: "https://github.com/new?template_owner=nightscout",
      installUrl: "https://github.com/apps/nocturne/installations/new?state=s",
    });
    render(AppBuildsPage);

    await expect
      .element(
        browserPage.getByText(
          "The Nocturne server does not receive the key or its passwords."
        )
      )
      .toBeVisible();
    await button("Connect a repository").click();

    await expect
      .element(browserPage.getByRole("link", { name: "Make the repository" }))
      .toHaveAttribute(
        "href",
        "https://github.com/new?template_owner=nightscout"
      );
    await expect
      .element(
        browserPage.getByRole("link", { name: "Connect the repository" })
      )
      .toHaveAttribute(
        "href",
        "https://github.com/apps/nocturne/installations/new?state=s"
      );
    expect(appBuildsMocks.begin).toHaveBeenCalledWith("aaps");
  });

  it("lets the linker pick a repository when none is chosen", async () => {
    linked(link(AppBuildLinkState.RepositoryNotChosen));
    appBuildsMocks.repositories.mockReturnValue(
      remoteQuery(() => [{ fullName: "octo/my-aaps", isPrivate: true }])
    );
    render(AppBuildsPage);

    await expect.element(button("Use this repository")).toBeDisabled();
    await expect
      .element(browserPage.getByText("Select a repository").first())
      .toBeVisible();
    expect(appBuildsMocks.repositories).toHaveBeenCalledWith("aaps");
  });

  it("shows each repository problem and offers to connect again", async () => {
    linked(
      link(AppBuildLinkState.Invalid, {
        validation: {
          problems: [
            AppBuildRepositoryProblem.NotPrivate,
            AppBuildRepositoryProblem.WrongTemplate,
          ],
        },
      })
    );
    render(AppBuildsPage);

    await expect
      .element(
        browserPage.getByText(
          "The repository is public. Make it private on GitHub."
        )
      )
      .toBeVisible();
    await expect
      .element(
        browserPage.getByText("was not made from the AAPS Builder template")
      )
      .toBeVisible();
    await expect.element(button("Connect again")).toBeVisible();
  });

  it("says why GitHub could not be read, and offers to connect again", async () => {
    linked(link(AppBuildLinkState.Unreachable, { error: "installation_gone" }));
    render(AppBuildsPage);

    await expect
      .element(
        browserPage.getByText(
          "The Nocturne GitHub app is not installed on the repository now. Connect the repository again."
        )
      )
      .toBeVisible();
    await expect.element(button("Connect again")).toBeVisible();
  });

  it("says the linker lost the admin role, and offers to connect again", async () => {
    linked(link(AppBuildLinkState.LinkerNotAdmin));
    render(AppBuildsPage);

    await expect
      .element(browserPage.getByText("is not an admin of it now"))
      .toBeVisible();
    await expect.element(button("Connect again")).toBeVisible();
  });

  it("asks the linker for the signing key while it is missing", async () => {
    linked(link(AppBuildLinkState.SecretsMissing));
    render(AppBuildsPage);

    await expect.element(button("Import my key")).toBeVisible();
    await expect.element(button("Make a new key")).toBeVisible();
    await expect
      .element(browserPage.getByText("import the key that you use now"))
      .toBeVisible();
  });

  it("starts a build with the target's inputs and lists the runs with their GitHub pages", async () => {
    linked(link(AppBuildLinkState.Ready));
    appBuildsMocks.runs.mockReturnValue(
      remoteQuery(() => [
        run(7, AppBuildRunState.Succeeded, "AAPS 3.3.2 fullRelease"),
      ])
    );
    appBuildsMocks.dispatch.mockResolvedValue({
      runsUrl: "https://github.com/octo/my-aaps/actions",
    });
    render(AppBuildsPage);

    await expect
      .element(browserPage.getByLabelText("AAPS version"))
      .toHaveValue("latest");
    await expect
      .element(browserPage.getByText("AAPS 3.3.2 fullRelease"))
      .toBeVisible();
    await expect.element(browserPage.getByText("Succeeded")).toBeVisible();
    await expect
      .element(browserPage.getByRole("link", { name: "Open on GitHub" }))
      .toHaveAttribute(
        "href",
        "https://github.com/octo/my-aaps/actions/runs/7"
      );
    await expect
      .element(browserPage.getByText("GitHub keeps the file for 14 days"))
      .toBeVisible();

    await button("Start build").click();

    await expect
      .element(browserPage.getByText("The build started"))
      .toBeVisible();
    expect(appBuildsMocks.dispatch).toHaveBeenCalledWith({
      targetId: "aaps",
      request: { inputs: { version: "latest", variant: "fullRelease" } },
    });
  });

  it("gives a person who did not link it no write controls, and says who can act", async () => {
    for (const state of [
      AppBuildLinkState.RepositoryNotChosen,
      AppBuildLinkState.SecretsMissing,
      AppBuildLinkState.Ready,
    ]) {
      linked(link(state, { linkedByCaller: false }));
      appBuildsMocks.runs.mockReturnValue(
        remoteQuery(() => [run(3, AppBuildRunState.Failed)])
      );
      const screen = render(AppBuildsPage);

      await expect
        .element(
          browserPage.getByText(
            "Only the person who connected this repository can change it or start a build"
          )
        )
        .toBeVisible();
      await expect.element(button("Connect my own repository")).toBeVisible();
      await expect.element(button("Disconnect the repository")).toBeVisible();
      for (const write of [
        "Use this repository",
        "Import my key",
        "Make a new key",
        "Save the key to GitHub",
        "Start build",
      ]) {
        await expect.element(button(write)).not.toBeInTheDocument();
      }
      screen.unmount();
    }
  });

  it("disconnects and then shows where the GitHub app is removed", async () => {
    linked(link(AppBuildLinkState.Ready));
    appBuildsMocks.unlink.mockResolvedValue({
      uninstallUrl: "https://github.com/settings/installations/42",
    });
    render(AppBuildsPage);

    await button("Disconnect the repository").click();
    await button("Disconnect").click();

    await expect
      .element(
        browserPage.getByRole("link", { name: "Open the GitHub app settings" })
      )
      .toHaveAttribute("href", "https://github.com/settings/installations/42");
    expect(appBuildsMocks.unlink).toHaveBeenCalledWith("aaps");
  });

  it("clears the callback query once shown, and drops the banner on a disconnect", async () => {
    linked(link(AppBuildLinkState.Ready));
    appBuildsMocks.unlink.mockResolvedValue({
      uninstallUrl: "https://github.com/settings/installations/42",
    });
    page.url = new URL(
      "http://localhost/settings/app-builds?target=aaps&linked=success"
    );
    render(AppBuildsPage, { linked: "success" });

    await expect
      .element(
        browserPage.getByText("The repository is connected to Nocturne.")
      )
      .toBeVisible();
    expect(page.url.search).toBe("");

    await button("Disconnect the repository").click();
    await button("Disconnect").click();

    await expect
      .element(
        browserPage.getByText("The repository is connected to Nocturne.")
      )
      .not.toBeInTheDocument();
  });

  it("lets the linker replace the signing key of a ready repository", async () => {
    linked(link(AppBuildLinkState.Ready));
    appBuildsMocks.runs.mockReturnValue(remoteQuery(() => []));
    render(AppBuildsPage);

    await expect.element(button("Make a new key")).not.toBeInTheDocument();
    await button("Replace the signing key").click();

    await expect.element(button("Import my key")).toBeVisible();
    await expect.element(button("Make a new key")).toBeVisible();
    await button("Cancel").click();
    await expect.element(button("Import my key")).not.toBeInTheDocument();
  });

  it("does not offer a person who did not link it to replace the key", async () => {
    linked(link(AppBuildLinkState.Ready, { linkedByCaller: false }));
    render(AppBuildsPage);

    await expect
      .element(browserPage.getByText("Recent builds").first())
      .toBeVisible();
    await expect
      .element(button("Replace the signing key"))
      .not.toBeInTheDocument();
  });

  it("says when no repository can be chosen, and offers to connect again", async () => {
    linked(link(AppBuildLinkState.RepositoryNotChosen));
    appBuildsMocks.repositories.mockReturnValue(remoteQuery(() => []));
    render(AppBuildsPage);

    await expect
      .element(
        browserPage.getByText(
          "cannot get access to a repository that you are an admin of"
        )
      )
      .toBeVisible();
    await expect.element(button("Use this repository")).not.toBeInTheDocument();
    await expect.element(button("Connect again")).toBeVisible();
  });

  it("does not render an address that is not https as a link", async () => {
    linked(
      link(AppBuildLinkState.Ready, { repositoryUrl: "javascript:alert(1)" })
    );
    appBuildsMocks.runs.mockReturnValue(
      remoteQuery(() => [
        {
          ...run(4, AppBuildRunState.Failed),
          htmlUrl: "http://github.com/octo/my-aaps/actions/runs/4",
        },
      ])
    );
    render(AppBuildsPage);

    await expect.element(browserPage.getByText("octo/my-aaps")).toBeVisible();
    await expect
      .element(browserPage.getByRole("link", { name: "octo/my-aaps" }))
      .not.toBeInTheDocument();
    await expect
      .element(browserPage.getByRole("link", { name: "Open on GitHub" }))
      .not.toBeInTheDocument();
  });

  it("refuses connect links that are not https", async () => {
    showStatus({ configured: true, targets: [AAPS], links: [] });
    appBuildsMocks.begin.mockResolvedValue({
      createRepositoryUrl: "https://github.com/new",
      installUrl: "javascript:alert(1)",
    });
    render(AppBuildsPage);

    await button("Connect a repository").click();

    await expect.element(browserPage.getByText("not secure")).toBeVisible();
    await expect
      .element(
        browserPage.getByRole("link", { name: "Connect the repository" })
      )
      .not.toBeInTheDocument();
  });

  it("names a target from its id, and does not offer a signing step it cannot make", async () => {
    const other: BuildTarget = {
      ...AAPS,
      id: "other",
      secrets: [{ name: "OTHER_SECRET", required: true }],
    };
    showStatus({
      configured: true,
      targets: [other],
      links: [link(AppBuildLinkState.SecretsMissing, { targetId: "other" })],
    });
    render(AppBuildsPage);

    await expect
      .element(
        browserPage.getByText(
          "This page cannot save the signing key for this app."
        )
      )
      .toBeVisible();
    await expect.element(button("Make a new key")).not.toBeInTheDocument();
    await expect
      .element(browserPage.getByText("AAPS", { exact: true }))
      .not.toBeInTheDocument();
  });

  it("shows the callback outcome", async () => {
    showStatus({ configured: true, targets: [AAPS], links: [] });
    const success = render(AppBuildsPage, { linked: "success" });
    await expect
      .element(
        browserPage.getByText("The repository is connected to Nocturne.")
      )
      .toBeVisible();
    success.unmount();

    const failed = render(AppBuildsPage, { error: "authorization_denied" });
    await expect
      .element(
        browserPage.getByText(
          "GitHub did not give access. Connect again, and approve the request on GitHub."
        )
      )
      .toBeVisible();
    failed.unmount();

    render(AppBuildsPage, { error: "something_new" });
    await expect
      .element(
        browserPage.getByText(
          "Nocturne could not connect the repository. Try again."
        )
      )
      .toBeVisible();
  });

  it("words a refused command from its code", async () => {
    linked(link(AppBuildLinkState.Ready));
    appBuildsMocks.dispatch.mockRejectedValue(refused(403, "linker_not_admin"));
    render(AppBuildsPage);

    await button("Start build").click();

    await expect
      .element(browserPage.getByText("is not an admin of it now"))
      .toBeVisible();
  });

  it("words a status read that failed", async () => {
    appBuildsMocks.status.mockReturnValue(
      remoteQueryError(refused(500, "github_unavailable"))
    );
    render(AppBuildsPage);

    await expect
      .element(
        browserPage.getByText("GitHub did not respond. Try again later.")
      )
      .toBeVisible();
  });

  it("does not read the status for a member without the site settings permission", async () => {
    page.data.effectivePermissions = ["glucose.read"];
    render(AppBuildsPage);

    await expect
      .element(
        browserPage.getByText(
          "You must have permission to change the site settings"
        )
      )
      .toBeVisible();
    expect(appBuildsMocks.status).not.toHaveBeenCalled();
  });
});

describe("Signing key step", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    page.data.effectivePermissions = ["*"];
    linked(link(AppBuildLinkState.SecretsMissing));
  });

  it("sends GitHub only the sealed value and key id, and only after the backup is downloaded", async () => {
    await sodium.ready;
    const repositoryKey = sodium.crypto_box_keypair();
    appBuildsMocks.secretKey.mockReturnValue(
      createQueryResource(async () => ({
        keyId: "568250167242549743",
        key: sodium.to_base64(
          repositoryKey.publicKey,
          sodium.base64_variants.ORIGINAL
        ),
      }))
    );
    appBuildsMocks.put.mockResolvedValue({
      configured: true,
      targets: [AAPS],
      links: [],
    });
    const password = "Secret#Pass1";
    render(AppBuildsPage);

    await button("Make a new key").click();
    await browserPage.getByLabelText("Password for the new key").fill(password);
    await browserPage.getByLabelText("Type the password again").fill(password);
    await button("Make the key").click();

    await expect
      .element(
        browserPage.getByText("Nocturne cannot recover the key or its password")
      )
      .toBeVisible();
    await expect.element(button("Save the key to GitHub")).toBeDisabled();

    await button("Download the backup file").click();
    await expect.element(button("Save the key to GitHub")).toBeEnabled();
    await button("Save the key to GitHub").click();

    await expect.poll(() => appBuildsMocks.put.mock.calls.length).toBe(1);
    expect(appBuildsMocks.secretKey).toHaveBeenCalledWith("aaps");
    const argument = appBuildsMocks.put.mock.calls[0][0] as {
      targetId: string;
      name: string;
      request: Record<string, string>;
    };
    expect(argument.targetId).toBe("aaps");
    expect(argument.name).toBe("KEYSTORE_SET");
    expect(Object.keys(argument.request).sort()).toEqual([
      "encryptedValue",
      "keyId",
    ]);
    expect(argument.request.keyId).toBe("568250167242549743");

    const packed = sodium.to_string(
      sodium.crypto_box_seal_open(
        sodium.from_base64(
          argument.request.encryptedValue,
          sodium.base64_variants.ORIGINAL
        ),
        repositoryKey.publicKey,
        repositoryKey.privateKey
      )
    );
    const fields = atob(packed).split("|");
    expect(fields.slice(1)).toEqual([password, "key0", password]);

    const sent = JSON.stringify(argument);
    expect(sent).not.toContain(password);
    expect(sent).not.toContain(packed);
    expect(sent).not.toContain(fields[0].slice(0, 64));

    for (const storage of [localStorage, sessionStorage]) {
      for (let i = 0; i < storage.length; i++) {
        expect(storage.getItem(storage.key(i)!) ?? "").not.toContain(password);
      }
    }
    expect(location.href).not.toContain(password);

    appBuildsMocks.unlink.mockResolvedValue({
      uninstallUrl: "https://github.com/settings/installations/42",
    });
    await button("Disconnect the repository").click();
    await button("Disconnect").click();
    await expect.poll(() => appBuildsMocks.unlink.mock.calls.length).toBe(1);
    for (const command of [
      appBuildsMocks.begin,
      appBuildsMocks.choose,
      appBuildsMocks.dispatch,
      appBuildsMocks.unlink,
    ]) {
      const calls = JSON.stringify(command.mock.calls);
      expect(calls).not.toContain(password);
      expect(calls).not.toContain(packed);
      expect(calls).not.toContain(fields[0].slice(0, 64));
    }
  });

  it("checks an imported keystore before it can be saved", async () => {
    render(AppBuildsPage);

    await button("Import my key").click();
    await browserPage.getByLabelText(/Keystore file/).upload(
      new File([new Uint8Array([1, 2, 3, 4])], "notes.jks", {
        type: "application/octet-stream",
      })
    );

    await expect
      .element(browserPage.getByText("This file is not a keystore file."))
      .toBeVisible();
    await expect
      .element(button("Save the key to GitHub"))
      .not.toBeInTheDocument();
  });

  it("accepts an existing keystore with its password, and drops the check when a field changes", async () => {
    const { createKeystore } = await import("#lib/app-builds/keystore.js");
    const existing = await createKeystore("Existing#1");
    render(AppBuildsPage);

    await button("Import my key").click();
    await browserPage
      .getByLabelText(/Keystore file/)
      .upload(new File([existing.bytes.slice()], "upload.p12"));
    await browserPage
      .getByLabelText("Keystore password", { exact: true })
      .fill("Wrong#Pass");
    await browserPage.getByLabelText("Key alias").fill("key0");
    await button("Check the key").click();
    await expect
      .element(browserPage.getByText("The keystore password is not correct."))
      .toBeVisible();

    await browserPage
      .getByLabelText("Keystore password", { exact: true })
      .fill("Existing#1");
    await button("Check the key").click();
    await expect
      .element(
        browserPage.getByText("The keystore and the passwords are correct.")
      )
      .toBeVisible();
    await expect.element(button("Save the key to GitHub")).toBeEnabled();

    await browserPage
      .getByLabelText("Keystore password", { exact: true })
      .fill("Existing#2");
    await expect
      .element(button("Save the key to GitHub"))
      .not.toBeInTheDocument();
  });
});

describe("Recent builds", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  it("refreshes while a run is queued or running, and stops when none is", async () => {
    const source = reactiveFields({ runs: [run(1, AppBuildRunState.Running)] });
    const resource = remoteQuery(() => source.runs);
    const refresh = vi.spyOn(resource, "refresh");
    appBuildsMocks.runs.mockReturnValue(resource);
    render(BuildRuns, { targetId: "aaps", pollMs: 20 });

    await expect
      .poll(() => refresh.mock.calls.length)
      .toBeGreaterThanOrEqual(3);

    source.runs = [run(1, AppBuildRunState.Succeeded)];
    await expect.element(browserPage.getByText("Succeeded")).toBeVisible();
    const settled = refresh.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, 150));

    expect(refresh.mock.calls.length).toBe(settled);
  });

  it("does not refresh when nothing is in progress", async () => {
    const resource = remoteQuery(() => [run(2, AppBuildRunState.Failed)]);
    const refresh = vi.spyOn(resource, "refresh");
    appBuildsMocks.runs.mockReturnValue(resource);
    render(BuildRuns, { targetId: "aaps", pollMs: 20 });

    await expect.element(browserPage.getByText("Failed")).toBeVisible();
    await new Promise((resolve) => setTimeout(resolve, 150));

    expect(refresh).not.toHaveBeenCalled();
  });

  it("refreshes while a run is expected, with nothing in progress yet, and stops after", async () => {
    const resource = remoteQuery(() => [run(2, AppBuildRunState.Failed)]);
    const refresh = vi.spyOn(resource, "refresh");
    appBuildsMocks.runs.mockReturnValue(resource);
    const screen = render(BuildRuns, {
      targetId: "aaps",
      pollMs: 20,
      expectingRun: true,
    });

    await expect
      .poll(() => refresh.mock.calls.length)
      .toBeGreaterThanOrEqual(3);

    await screen.rerender({
      targetId: "aaps",
      pollMs: 20,
      expectingRun: false,
    });
    const settled = refresh.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, 150));

    expect(refresh.mock.calls.length).toBe(settled);
  });

  it("expects a run for two minutes after a build starts", async () => {
    page.data.effectivePermissions = ["*"];
    linked(link(AppBuildLinkState.Ready));
    const resource = remoteQuery(() => [run(2, AppBuildRunState.Succeeded)]);
    const refresh = vi.spyOn(resource, "refresh");
    appBuildsMocks.runs.mockReturnValue(resource);
    appBuildsMocks.dispatch.mockResolvedValue({
      runsUrl: "https://github.com/octo/my-aaps/actions",
    });
    render(AppBuildsPage);
    await expect.element(button("Start build")).toBeVisible();

    const refreshArmed = () =>
      appBuildsMocks.dispatch.mock.calls.length > 0 && vi.getTimerCount() > 0;
    vi.useFakeTimers({
      toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"],
    });
    try {
      await button("Start build").click();
      for (let i = 0; i < 100 && !refreshArmed(); i++) await Promise.resolve();
      flushSync();

      vi.advanceTimersByTime(10_000);
      expect(refresh).toHaveBeenCalledTimes(1);
      vi.advanceTimersByTime(100_000);
      const beforeWindowEnds = refresh.mock.calls.length;
      expect(beforeWindowEnds).toBeGreaterThan(1);

      vi.advanceTimersByTime(20_000);
      flushSync();
      const atWindowEnd = refresh.mock.calls.length;
      vi.advanceTimersByTime(60_000);
      expect(refresh.mock.calls.length).toBe(atWindowEnd);
    } finally {
      vi.useRealTimers();
      delete page.data.effectivePermissions;
    }
  });
});
