/**
 * The signing keystore for an AAPS Builder repository, made or checked in the
 * browser, and the `KEYSTORE_SET` secret packed from it. Nothing here leaves
 * the browser: the caller seals the packed value (`sealed-secret.ts`) before
 * anything is sent.
 *
 * Ported from the inline script of the AAPS documentation's "AAPS Builder setup
 * page".
 *
 * - Page:
 *   https://androidaps.readthedocs.io/en/latest/SettingUpAaps/BrowserBuildAapsBuilderSetup.html
 * - Source: https://github.com/nightscout/AndroidAPSdocs
 * - Based on: AAPS Builder by the AAPS Builder authors,
 *   https://github.com/dio99/aaps-builder
 * - Licence: GNU Affero General Public License v3.0 (AGPL-3.0)
 *
 * The checks, password rules, key parameters and packing are upstream's. The
 * Google Drive step is left out, and problems are returned as codes for the
 * page to word.
 */
import forge from "node-forge";

/** The alias upstream gives a new key. */
export const NEW_KEY_ALIAS = "key0";

/**
 * Upstream's backup file name. The file is PKCS12, which keytool and Android
 * Studio read by content.
 */
export const BACKUP_FILE_NAME = "aaps-keystore.jks";

/**
 * The largest keystore file accepted. Sealed and base64-encoded, the packed
 * secret grows to about 2.4 times the file, and the API refuses a sealed value
 * over 65,536 characters (`SealedAppBuildSecret.EncryptedValue`).
 */
export const MAX_KEYSTORE_BYTES = 24 * 1024;

export interface SigningKeystore {
  bytes: Uint8Array;
  storePassword: string;
  alias: string;
  keyPassword: string;
}

export type NewPasswordProblem =
  | "too_short"
  | "mismatch"
  | "not_printable_ascii";

export type ImportProblem =
  | "not_keystore"
  | "too_large"
  | "empty"
  | "contains_pipe"
  | "wrong_store_password"
  | "alias_not_found"
  | "wrong_key_password";

export type ImportResult =
  | { ok: true; keystore: SigningKeystore; checked: boolean }
  | { ok: false; problem: ImportProblem; aliases?: string[] };

/**
 * What a file holds, read without a password: JKS with its key aliases, or (by
 * its DER start) PKCS12.
 */
export type KeystoreFormat =
  | { kind: "jks"; aliases: string[] }
  | { kind: "pkcs12"; aliases: string[] };

// The workflow reads the four fields with `cut -d'|'`.
const SEPARATOR = "|";

forge.random.getBytesSync = (count: number) =>
  forge.util.binary.raw.encode(crypto.getRandomValues(new Uint8Array(count)));
// forge's own callers use the synchronous return value, which the typings declare as void.
forge.random.getBytes = (count, callback) => {
  const bytes = forge.random.getBytesSync(count);
  callback?.(null, bytes);
  return bytes;
};

/**
 * Upstream's rules for a new password, which is both the store and the key
 * password.
 */
export function newPasswordProblem(
  password: string,
  confirmation: string
): NewPasswordProblem | null {
  if (password.length < 8) return "too_short";
  // eslint-disable-next-line security/detect-possible-timing-attacks -- both are typed in this browser; nothing remote times it.
  if (password !== confirmation) return "mismatch";
  if (!/^[\x21-\x7e]+$/.test(password) || password.includes(SEPARATOR))
    return "not_printable_ascii";
  return null;
}

/**
 * A new RSA 2048 key with a self-signed 50-year certificate, in a PKCS12 store
 * under {@link NEW_KEY_ALIAS}.
 */
export async function createKeystore(
  password: string
): Promise<SigningKeystore> {
  if (newPasswordProblem(password, password))
    throw new Error("password_rejected");

  const pair = await crypto.subtle.generateKey(
    {
      name: "RSASSA-PKCS1-v1_5",
      modulusLength: 2048,
      publicExponent: new Uint8Array([1, 0, 1]),
      hash: "SHA-256",
    },
    true,
    ["sign", "verify"]
  );
  const pkcs8 = new Uint8Array(
    await crypto.subtle.exportKey("pkcs8", pair.privateKey)
  );
  const key = forge.pki.privateKeyFromAsn1(fromDer(pkcs8));

  const cert = forge.pki.createCertificate();
  cert.publicKey = forge.pki.setRsaPublicKey(key.n, key.e);
  cert.serialNumber =
    "01" + forge.util.bytesToHex(forge.random.getBytesSync(8));
  cert.validity.notBefore = new Date();
  cert.validity.notAfter = new Date();
  cert.validity.notAfter.setFullYear(
    cert.validity.notBefore.getFullYear() + 50
  );
  const subject = [{ name: "commonName", value: "AAPS" }];
  cert.setSubject(subject);
  cert.setIssuer(subject);
  cert.sign(key, forge.md.sha256.create());

  const asn = forge.pkcs12.toPkcs12Asn1(key, [cert], password, {
    algorithm: "3des",
    friendlyName: NEW_KEY_ALIAS,
    generateLocalKeyId: true,
  });

  return {
    bytes: forge.util.binary.raw.decode(forge.asn1.toDer(asn).getBytes()),
    storePassword: password,
    alias: NEW_KEY_ALIAS,
    keyPassword: password,
  };
}

export function keystoreFormat(bytes: Uint8Array): KeystoreFormat | null {
  const jks = tryParseJks(bytes);
  if (jks)
    return { kind: "jks", aliases: jks.entries.map((entry) => entry.alias) };
  if (bytes[0] === 0x30)
    return { kind: "pkcs12", aliases: pkcs12FriendlyNames(bytes) };
  return null;
}

const DATA_OID = "1.2.840.113549.1.7.1";
const FRIENDLY_NAME_OID = "1.2.840.113549.1.9.20";

/**
 * The friendly names a PKCS12 file shows without its password: those of the
 * bags in unencrypted SafeContents, where keytool and forge put the key bag.
 * Empty when there are none or the file cannot be read.
 */
function pkcs12FriendlyNames(bytes: Uint8Array): string[] {
  try {
    const pfx = children(fromDer(bytes));
    const names = new Set<string>();
    for (const contentInfo of children(asn1FromOctets(dataContent(pfx[1])))) {
      if (oidOf(children(contentInfo)[0]) !== DATA_OID) continue;
      for (const bag of children(asn1FromOctets(dataContent(contentInfo)))) {
        for (const attribute of children(children(bag)[2])) {
          const [oid, values] = children(attribute);
          if (oidOf(oid) !== FRIENDLY_NAME_OID) continue;
          for (const value of children(values)) {
            // forge decodes a BMPString to a JS string.
            if (typeof value.value === "string") names.add(value.value);
          }
        }
      }
    }
    return [...names];
  } catch {
    return [];
  }
}

function children(node: forge.asn1.Asn1 | undefined): forge.asn1.Asn1[] {
  return node && Array.isArray(node.value) ? node.value : [];
}

function oidOf(node: forge.asn1.Asn1 | undefined): string | undefined {
  return node && typeof node.value === "string"
    ? forge.asn1.derToOid(node.value)
    : undefined;
}

/**
 * The OCTET STRING of a `data` ContentInfo's `[0]` content, joined when BER
 * split it into parts.
 */
function dataContent(contentInfo: forge.asn1.Asn1 | undefined): string {
  const octets = children(children(contentInfo)[1])[0];
  if (!octets) return "";
  if (typeof octets.value === "string") return octets.value;
  return children(octets)
    .map((part) => (typeof part.value === "string" ? part.value : ""))
    .join("");
}

function asn1FromOctets(octets: string): forge.asn1.Asn1 | undefined {
  return octets
    ? forge.asn1.fromDer(forge.util.createBuffer(octets))
    : undefined;
}

/**
 * Checks an existing keystore as upstream does.
 *
 * - JKS: the store password against the file's digest, then the alias, then the
 *   key password against the key's own check value. The alias match ignores
 *   case and keeps the stored spelling.
 * - PKCS12: the store password against the MAC, and the alias against the bags'
 *   friendly names.
 *
 * A PKCS12 file forge cannot read for another reason is accepted unchecked
 * (`checked: false`). The build's own `jarsigner` step checks it.
 */
export async function importKeystore(
  bytes: Uint8Array,
  storePassword: string,
  alias: string,
  keyPassword: string
): Promise<ImportResult> {
  if (bytes.length > MAX_KEYSTORE_BYTES)
    return { ok: false, problem: "too_large" };
  const format = keystoreFormat(bytes);
  if (!format) return { ok: false, problem: "not_keystore" };

  alias = alias.trim();
  if (!storePassword || !alias || !keyPassword)
    return { ok: false, problem: "empty" };
  if (
    [storePassword, alias, keyPassword].some((value) =>
      value.includes(SEPARATOR)
    )
  ) {
    return { ok: false, problem: "contains_pipe" };
  }

  const accept = (finalAlias: string, checked: boolean): ImportResult => ({
    ok: true,
    checked,
    keystore: { bytes, storePassword, alias: finalAlias, keyPassword },
  });

  try {
    if (format.kind === "jks") {
      const jks = tryParseJks(bytes);
      if (!jks) return { ok: false, problem: "not_keystore" };
      const digest = await sha1(
        utf16be(storePassword),
        utf8("Mighty Aphrodite"),
        jks.body
      );
      if (!equalBytes(digest, jks.digest))
        return { ok: false, problem: "wrong_store_password" };

      const entry = jks.entries.find(
        (e) => e.alias.toLowerCase() === alias.toLowerCase()
      );
      if (!entry) {
        return {
          ok: false,
          problem: "alias_not_found",
          aliases: jks.entries.map((e) => e.alias),
        };
      }
      if (!(await jksKeyPasswordMatches(entry.key, keyPassword))) {
        return { ok: false, problem: "wrong_key_password" };
      }
      return accept(entry.alias, true);
    }

    let p12: forge.pkcs12.Pkcs12Pfx;
    try {
      p12 = forge.pkcs12.pkcs12FromAsn1(fromDer(bytes), storePassword);
    } catch (e) {
      if (/password|MAC/i.test(String(e instanceof Error ? e.message : e))) {
        return { ok: false, problem: "wrong_store_password" };
      }
      return accept(alias, false);
    }

    const names = [
      ...new Set(
        p12.safeContents.flatMap((contents) =>
          contents.safeBags.flatMap((bag) =>
            friendlyNames(bag.attributes.friendlyName)
          )
        )
      ),
    ];
    if (
      names.length > 0 &&
      !names.some((name) => name.toLowerCase() === alias.toLowerCase())
    ) {
      return { ok: false, problem: "alias_not_found", aliases: names };
    }
    return accept(alias, true);
  } catch {
    return accept(alias, false);
  }
}

/**
 * The `KEYSTORE_SET` value: base64 of
 * `keystoreBase64|storePassword|alias|keyPassword`, which the AAPS Builder
 * workflow splits with `cut -d'|' -f1..4` after `base64 -d`.
 *
 * It refuses a field the workflow would read back differently. A separator
 * moves the cut. A line break ends the `$GITHUB_ENV` line that the workflow
 * writes each value to.
 */
export function packKeystoreSet(keystore: SigningKeystore): string {
  const fields = [keystore.storePassword, keystore.alias, keystore.keyPassword];
  if (fields.some((field) => field.length === 0 || /[|\r\n]/.test(field))) {
    throw new Error("keystore_field_rejected");
  }
  return toBase64(utf8([toBase64(keystore.bytes), ...fields].join(SEPARATOR)));
}

interface JksStore {
  entries: { alias: string; key: Uint8Array }[];
  body: Uint8Array;
  digest: Uint8Array;
}

function friendlyNames(value: unknown): string[] {
  return Array.isArray(value)
    ? value.filter((name): name is string => typeof name === "string")
    : [];
}

function tryParseJks(bytes: Uint8Array): JksStore | null {
  try {
    return parseJks(bytes);
  } catch {
    return null;
  }
}

function parseJks(bytes: Uint8Array): JksStore | null {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (view.getUint32(0) !== 0xfeedfeed) return null;

  let offset = 12;
  const count = view.getUint32(8);
  const entries: JksStore["entries"] = [];
  const readUtf = () => {
    const length = view.getUint16(offset);
    offset += 2;
    const text = new TextDecoder().decode(
      bytes.subarray(offset, offset + length)
    );
    offset += length;
    return text;
  };
  const skipCert = () => {
    if (view.getUint32(4) === 2) readUtf();
    const length = view.getUint32(offset);
    offset += 4 + length;
  };

  for (let i = 0; i < count; i++) {
    const tag = view.getUint32(offset);
    offset += 4;
    const alias = readUtf();
    offset += 8;
    if (tag === 1) {
      const length = view.getUint32(offset);
      offset += 4;
      const key = bytes.subarray(offset, offset + length);
      offset += length;
      const chain = view.getUint32(offset);
      offset += 4;
      for (let c = 0; c < chain; c++) skipCert();
      entries.push({ alias, key });
    } else {
      skipCert();
    }
  }

  return {
    entries,
    body: bytes.subarray(0, offset),
    digest: bytes.subarray(offset, offset + 20),
  };
}

/**
 * Sun's JKS KeyProtector: decrypts the key with the SHA-1 keystream and
 * compares its check value.
 */
async function jksKeyPasswordMatches(
  encryptedKeyInfo: Uint8Array,
  password: string
): Promise<boolean> {
  const parts = fromDer(encryptedKeyInfo).value;
  const octets = Array.isArray(parts) ? parts[1]?.value : undefined;
  if (typeof octets !== "string") return false;
  const data = forge.util.binary.raw.decode(octets);
  const salt = data.subarray(0, 20);
  const encrypted = data.subarray(20, data.length - 20);
  const check = data.subarray(data.length - 20);
  const passwordBytes = utf16be(password);

  const plain = new Uint8Array(encrypted.length);
  let digest = salt;
  for (let offset = 0; offset < encrypted.length; offset += 20) {
    digest = await sha1(passwordBytes, digest);
    for (let j = 0; j < 20 && offset + j < encrypted.length; j++) {
      plain[offset + j] = encrypted[offset + j] ^ digest[j];
    }
  }
  return equalBytes(await sha1(passwordBytes, plain), check);
}

function fromDer(bytes: Uint8Array): forge.asn1.Asn1 {
  return forge.asn1.fromDer(
    forge.util.createBuffer(forge.util.binary.raw.encode(bytes))
  );
}

async function sha1(...parts: Uint8Array[]): Promise<Uint8Array> {
  const all = new Uint8Array(
    parts.reduce((total, part) => total + part.length, 0)
  );
  let offset = 0;
  for (const part of parts) {
    all.set(part, offset);
    offset += part.length;
  }
  return new Uint8Array(await crypto.subtle.digest("SHA-1", all));
}

function equalBytes(a: Uint8Array, b: Uint8Array): boolean {
  return a.length === b.length && a.every((value, i) => value === b[i]);
}

function utf8(text: string): Uint8Array {
  return new TextEncoder().encode(text);
}

/** JKS hashes passwords as UTF-16BE. */
function utf16be(text: string): Uint8Array {
  const bytes = new Uint8Array(text.length * 2);
  for (let i = 0; i < text.length; i++) {
    bytes[2 * i] = text.charCodeAt(i) >> 8;
    bytes[2 * i + 1] = text.charCodeAt(i) & 255;
  }
  return bytes;
}

function toBase64(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  return btoa(binary);
}
