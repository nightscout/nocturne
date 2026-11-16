import { describe, expect, it } from "vitest";
import sodium from "libsodium-wrappers";
import { sealSecret } from "./sealed-secret";

describe("sealSecret", () => {
  it("seals a value that only the repository's private key opens", async () => {
    await sodium.ready;
    const pair = sodium.crypto_box_keypair();
    const publicKey = sodium.to_base64(
      pair.publicKey,
      sodium.base64_variants.ORIGINAL
    );
    const plaintext = "a2V5c3RvcmU=|store#Pass1|key0|store#Pass1";

    const sealed = await sealSecret(plaintext, publicKey);
    const opened = sodium.crypto_box_seal_open(
      sodium.from_base64(sealed, sodium.base64_variants.ORIGINAL),
      pair.publicKey,
      pair.privateKey
    );

    expect(sodium.to_string(opened)).toBe(plaintext);
    expect(sealed).not.toContain("store#Pass1");
    expect(Buffer.from(sealed, "base64").toString("latin1")).not.toContain(
      "store#Pass1"
    );
    expect(await sealSecret(plaintext, publicKey)).not.toBe(sealed);
  });

  it("does not open with another key", async () => {
    await sodium.ready;
    const pair = sodium.crypto_box_keypair();
    const other = sodium.crypto_box_keypair();

    const sealed = await sealSecret(
      "value",
      sodium.to_base64(pair.publicKey, sodium.base64_variants.ORIGINAL)
    );

    expect(() =>
      sodium.crypto_box_seal_open(
        sodium.from_base64(sealed, sodium.base64_variants.ORIGINAL),
        other.publicKey,
        other.privateKey
      )
    ).toThrow();
  });
});
