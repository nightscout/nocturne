import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import sodium from "libsodium-wrappers";
import * as keystore from "#lib/app-builds/keystore.js";
import type { SigningKeystore } from "#lib/app-builds/keystore.js";
import { appBuildsMocks } from "#lib/test-stubs/app-builds.js";
import { createQueryResource } from "#lib/test-stubs/remote-resource.js";

import SigningKeyStep from "./SigningKeyStep.svelte";

vi.mock(
  "#lib/api/generated/appBuilds.generated.remote.js",
  () => import("#lib/test-stubs/app-builds.js")
);
vi.mock("#lib/app-builds/keystore.js", async (importOriginal) => {
  const actual =
    await importOriginal<typeof import("#lib/app-builds/keystore.js")>();
  return {
    ...actual,
    importKeystore: vi.fn(actual.importKeystore),
    createKeystore: vi.fn(actual.createKeystore),
  };
});

const PROPS = { targetId: "aaps", secretName: "KEYSTORE_SET", appName: "AAPS" };
const PASSWORD = "Secret#Pass1";

const button = (name: string) =>
  page.getByRole("button", { name, exact: true });
const isZeroed = (bytes: Uint8Array) => bytes.every((byte) => byte === 0);
const passwordFieldValues = () =>
  [
    ...document.querySelectorAll<HTMLInputElement>('input[type="password"]'),
  ].map((input) => input.value);

/** A promise and the function that settles it, for holding an async step open. */
function gate() {
  let open!: () => void;
  const opened = new Promise<void>((resolve) => (open = resolve));
  return { opened, open };
}

async function repositoryKey() {
  await sodium.ready;
  const pair = sodium.crypto_box_keypair();
  const fetchKey = vi.fn(async () => ({
    keyId: "568250167242549743",
    key: sodium.to_base64(pair.publicKey, sodium.base64_variants.ORIGINAL),
  }));
  appBuildsMocks.secretKey.mockReturnValue(createQueryResource(fetchKey));
  return { pair, fetchKey };
}

async function makeKeyAndDownload() {
  await button("Make a new key").click();
  await page.getByLabelText("Password for the new key").fill(PASSWORD);
  await page.getByLabelText("Type the password again").fill(PASSWORD);
  await button("Make the key").click();
  await button("Download the backup file").click();
}

describe("SigningKeyStep", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  it("keeps the sealed value after a failure a retry can fix, and sends it again without a second key fetch", async () => {
    const { fetchKey } = await repositoryKey();
    appBuildsMocks.put
      .mockRejectedValueOnce({
        status: 502,
        body: { message: "github_unavailable" },
      })
      .mockResolvedValueOnce({});
    const onSaved = vi.fn();
    render(SigningKeyStep, { ...PROPS, onSaved });

    await makeKeyAndDownload();
    await button("Save the key to GitHub").click();

    await expect
      .element(page.getByText("GitHub did not respond. Try again later."))
      .toBeVisible();
    expect(passwordFieldValues().every((value) => value === "")).toBe(true);
    await expect.element(button("Save the key to GitHub")).toBeEnabled();
    await button("Save the key to GitHub").click();

    await expect.poll(() => onSaved.mock.calls.length).toBe(1);
    expect(appBuildsMocks.put).toHaveBeenCalledTimes(2);
    expect(appBuildsMocks.put.mock.calls[1][0]).toEqual(
      appBuildsMocks.put.mock.calls[0][0]
    );
    expect(fetchKey).toHaveBeenCalledTimes(1);
  });

  it("drops the sealed value after a refusal and asks for the backup file", async () => {
    await repositoryKey();
    appBuildsMocks.put.mockRejectedValueOnce({
      status: 400,
      body: { message: "input_invalid" },
    });
    render(SigningKeyStep, PROPS);

    await makeKeyAndDownload();
    await button("Save the key to GitHub").click();

    await expect
      .element(page.getByText("import the backup file that you downloaded"))
      .toBeVisible();
    await expect
      .element(button("Save the key to GitHub"))
      .not.toBeInTheDocument();
    expect(appBuildsMocks.put).toHaveBeenCalledTimes(1);
  });

  it("disables the file and the mode buttons while a check runs", async () => {
    const existing = await keystore.createKeystore("Existing#1");
    const hold = gate();
    const actual = vi.mocked(keystore.importKeystore).getMockImplementation()!;
    vi.mocked(keystore.importKeystore).mockImplementationOnce(
      async (...args) => {
        await hold.opened;
        return actual(...args);
      }
    );
    render(SigningKeyStep, PROPS);

    await button("Import my key").click();
    await page
      .getByLabelText(/Keystore file/)
      .upload(new File([existing.bytes.slice()], "upload.p12"));
    await page
      .getByLabelText("Keystore password", { exact: true })
      .fill("Existing#1");
    await button("Check the key").click();

    await expect.element(page.getByLabelText(/Keystore file/)).toBeDisabled();
    await expect.element(button("Make a new key")).toBeDisabled();
    await expect.element(button("Import my key")).toBeDisabled();
    hold.open();
    await expect
      .element(page.getByText("The keystore and the passwords are correct."))
      .toBeVisible();
    await expect.element(button("Make a new key")).toBeEnabled();
  });

  it("prefills the alias of a PKCS12 file with one friendly name", async () => {
    const existing = await keystore.createKeystore("Existing#1");
    render(SigningKeyStep, PROPS);

    await button("Import my key").click();
    await page
      .getByLabelText(/Keystore file/)
      .upload(new File([existing.bytes.slice()], "upload.p12"));

    await expect.element(page.getByLabelText("Key alias")).toHaveValue("key0");
  });

  it("names the alias key0 in the caution for a new key", async () => {
    render(SigningKeyStep, PROPS);

    await button("Make a new key").click();
    await page.getByLabelText("Password for the new key").fill(PASSWORD);
    await page.getByLabelText("Type the password again").fill(PASSWORD);
    await button("Make the key").click();

    await expect
      .element(page.getByText("The alias of the key is key0."))
      .toBeVisible();
  });

  it("zeroes the held keystore on a mode switch and on unmount", async () => {
    const existing = await keystore.createKeystore("Existing#1");
    const results: SigningKeystore[] = [];
    const actual = vi.mocked(keystore.importKeystore).getMockImplementation()!;
    vi.mocked(keystore.importKeystore).mockImplementation(async (...args) => {
      const result = await actual(...args);
      if (result.ok) results.push(result.keystore);
      return result;
    });
    const screen = render(SigningKeyStep, PROPS);

    async function check() {
      await button("Import my key").click();
      await page
        .getByLabelText(/Keystore file/)
        .upload(new File([existing.bytes.slice()], "upload.p12"));
      await page
        .getByLabelText("Keystore password", { exact: true })
        .fill("Existing#1");
      await button("Check the key").click();
      await expect
        .element(page.getByText("The keystore and the passwords are correct."))
        .toBeVisible();
    }

    await check();
    await button("Make a new key").click();
    expect(isZeroed(results[0].bytes)).toBe(true);

    await check();
    screen.unmount();
    expect(isZeroed(results[1].bytes)).toBe(true);
  });

  it("zeroes and drops a new key that finishes after the step is left", async () => {
    const hold = gate();
    let late: SigningKeystore | undefined;
    const actual = vi.mocked(keystore.createKeystore).getMockImplementation()!;
    vi.mocked(keystore.createKeystore).mockImplementationOnce(
      async (password) => {
        late = await actual(password);
        await hold.opened;
        return late;
      }
    );
    const screen = render(SigningKeyStep, PROPS);

    await button("Make a new key").click();
    await page.getByLabelText("Password for the new key").fill(PASSWORD);
    await page.getByLabelText("Type the password again").fill(PASSWORD);
    await button("Make the key").click();
    await expect.poll(() => late).toBeDefined();

    screen.unmount();
    hold.open();

    await expect.poll(() => isZeroed(late!.bytes)).toBe(true);
  });
});
