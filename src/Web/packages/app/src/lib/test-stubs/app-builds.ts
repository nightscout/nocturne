import { vi } from "vitest";
import type {
  AppBuildDispatchResult,
  AppBuildLinkStart,
  AppBuildRepositoryOption,
  AppBuildRun,
  AppBuildSecretKey,
  AppBuildsStatus,
  AppBuildUnlinkResult,
} from "#lib/api/index.js";
import { remoteCommand, type RemoteQueryStub } from "./remote-resource";

/** Stand-ins for `#lib/api/generated/appBuilds.generated.remote.js`. */
export const appBuildsMocks = {
  status: vi.fn<() => RemoteQueryStub<AppBuildsStatus>>(),
  repositories:
    vi.fn<(targetId: string) => RemoteQueryStub<AppBuildRepositoryOption[]>>(),
  secretKey: vi.fn<(targetId: string) => RemoteQueryStub<AppBuildSecretKey>>(),
  runs: vi.fn<(targetId: string) => RemoteQueryStub<AppBuildRun[]>>(),
  begin: vi.fn<(targetId: unknown) => Promise<AppBuildLinkStart>>(),
  choose: vi.fn<(arg: unknown) => Promise<AppBuildsStatus>>(),
  put: vi.fn<(arg: unknown) => Promise<AppBuildsStatus>>(),
  dispatch: vi.fn<(arg: unknown) => Promise<AppBuildDispatchResult>>(),
  unlink: vi.fn<(targetId: unknown) => Promise<AppBuildUnlinkResult>>(),
};

export const getAppBuildsStatus = () => appBuildsMocks.status();
export const getAppBuildRepositories = (targetId: string) =>
  appBuildsMocks.repositories(targetId);
export const getAppBuildSecretKey = (targetId: string) =>
  appBuildsMocks.secretKey(targetId);
export const getAppBuildRuns = (targetId: string) =>
  appBuildsMocks.runs(targetId);
export const beginAppBuildLink = remoteCommand((arg) =>
  appBuildsMocks.begin(arg)
);
export const chooseAppBuildRepository = remoteCommand((arg) =>
  appBuildsMocks.choose(arg)
);
export const putAppBuildSecret = remoteCommand((arg) =>
  appBuildsMocks.put(arg)
);
export const dispatchAppBuild = remoteCommand((arg) =>
  appBuildsMocks.dispatch(arg)
);
export const unlinkAppBuild = remoteCommand((arg) =>
  appBuildsMocks.unlink(arg)
);
