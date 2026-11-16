import sodium from "libsodium-wrappers";

/**
 * Seals a value to a repository's Actions public key with a libsodium sealed
 * box (`crypto_box_seal`), as GitHub's secrets API requires. Only the holder of
 * the repository's private key, GitHub, can open it; the result is the base64
 * `encrypted_value`.
 */
export async function sealSecret(
  value: string,
  publicKeyBase64: string
): Promise<string> {
  await sodium.ready;
  const publicKey = sodium.from_base64(
    publicKeyBase64,
    sodium.base64_variants.ORIGINAL
  );
  const sealed = sodium.crypto_box_seal(sodium.from_string(value), publicKey);
  return sodium.to_base64(sealed, sodium.base64_variants.ORIGINAL);
}
