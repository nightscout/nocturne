import { describe, expect, it } from "vitest";
import forge from "node-forge";
import {
  createKeystore,
  importKeystore,
  keystoreFormat,
  MAX_KEYSTORE_BYTES,
  NEW_KEY_ALIAS,
  newPasswordProblem,
  packKeystoreSet,
  type SigningKeystore,
} from "./keystore";

/**
 * The AAPS Builder workflow's "Read keystore" step: strip whitespace, `base64
 * -d`, then `cut -d'|' -f1..4`. Like the step, it refuses a value under 200
 * characters, one without exactly three separators, and one with an empty alias
 * or key password.
 */
function decodeLikeTheWorkflow(secret: string) {
  const value = secret.replace(/[ \r\n]/g, "");
  if (value.length < 200) throw new Error("too short");
  const decoded = Buffer.from(value, "base64").toString("utf8");
  const cut = (field: number) => decoded.split("|")[field - 1] ?? "";
  if ((decoded.match(/\|/g) ?? []).length !== 3 || !cut(3) || !cut(4)) {
    throw new Error("not a keystore with passwords and alias");
  }
  return {
    keystore: new Uint8Array(Buffer.from(cut(1), "base64")),
    storePassword: cut(2),
    alias: cut(3),
    keyPassword: cut(4),
  };
}

const der = (bytes: Uint8Array) =>
  forge.asn1.fromDer(
    forge.util.createBuffer(forge.util.binary.raw.encode(bytes))
  );

async function rsaKey(): Promise<{
  pkcs8: Uint8Array;
  key: forge.pki.rsa.PrivateKey;
}> {
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
  return {
    pkcs8,
    key: forge.pki.privateKeyFromAsn1(der(pkcs8)) as forge.pki.rsa.PrivateKey,
  };
}

function selfSigned(key: forge.pki.rsa.PrivateKey): forge.pki.Certificate {
  const cert = forge.pki.createCertificate();
  cert.publicKey = forge.pki.setRsaPublicKey(key.n, key.e);
  cert.serialNumber = "01";
  cert.validity.notBefore = new Date();
  cert.validity.notAfter = new Date(Date.now() + 86_400_000);
  cert.setSubject([{ name: "commonName", value: "Test" }]);
  cert.setIssuer([{ name: "commonName", value: "Test" }]);
  cert.sign(key, forge.md.sha256.create());
  return cert;
}

async function pkcs12Fixture(
  password: string,
  alias: string
): Promise<Uint8Array> {
  const { key } = await rsaKey();
  const asn = forge.pkcs12.toPkcs12Asn1(key, [selfSigned(key)], password, {
    algorithm: "3des",
    friendlyName: alias,
  });
  return forge.util.binary.raw.decode(forge.asn1.toDer(asn).getBytes());
}

const sha1 = (...parts: Uint8Array[]) => {
  const md = forge.md.sha1.create();
  for (const part of parts) md.update(forge.util.binary.raw.encode(part));
  return forge.util.binary.raw.decode(md.digest().getBytes());
};

const utf16be = (text: string) =>
  new Uint8Array(
    [...text].flatMap((c) => [c.charCodeAt(0) >> 8, c.charCodeAt(0) & 255])
  );

function concat(...parts: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let offset = 0;
  for (const part of parts) {
    out.set(part, offset);
    offset += part.length;
  }
  return out;
}

const u16 = (n: number) => new Uint8Array([n >> 8, n & 255]);
const u32 = (n: number) =>
  new Uint8Array([n >>> 24, (n >> 16) & 255, (n >> 8) & 255, n & 255]);
const utf = (text: string) => {
  const bytes = new TextEncoder().encode(text);
  return concat(u16(bytes.length), bytes);
};

/**
 * A version 2 JKS file with one private-key entry, as
 * `sun.security.provider.JavaKeyStore` writes it. The key is under Sun's
 * KeyProtector (SHA-1 keystream, OID 1.3.6.1.4.1.42.2.17.1.1). The file is
 * under the "Mighty Aphrodite" SHA-1 digest. keytool 17 lists a file built this
 * way.
 */
async function jksFixture(
  storePassword: string,
  alias: string,
  keyPassword: string
): Promise<Uint8Array> {
  const { pkcs8, key } = await rsaKey();
  const salt = crypto.getRandomValues(new Uint8Array(20));
  const keyPasswordBytes = utf16be(keyPassword);
  const encrypted = new Uint8Array(pkcs8.length);
  let digest: Uint8Array = salt;
  for (let offset = 0; offset < pkcs8.length; offset += 20) {
    digest = sha1(keyPasswordBytes, digest);
    for (let j = 0; j < 20 && offset + j < pkcs8.length; j++)
      encrypted[offset + j] = pkcs8[offset + j] ^ digest[j];
  }
  const protectedKey = concat(salt, encrypted, sha1(keyPasswordBytes, pkcs8));

  const a = forge.asn1;
  const encryptedKeyInfo = a.create(a.Class.UNIVERSAL, a.Type.SEQUENCE, true, [
    a.create(a.Class.UNIVERSAL, a.Type.SEQUENCE, true, [
      a.create(
        a.Class.UNIVERSAL,
        a.Type.OID,
        false,
        a.oidToDer("1.3.6.1.4.1.42.2.17.1.1").getBytes()
      ),
      a.create(a.Class.UNIVERSAL, a.Type.NULL, false, ""),
    ]),
    a.create(
      a.Class.UNIVERSAL,
      a.Type.OCTETSTRING,
      false,
      forge.util.binary.raw.encode(protectedKey)
    ),
  ]);
  const keyDer = forge.util.binary.raw.decode(
    a.toDer(encryptedKeyInfo).getBytes()
  );
  const certDer = forge.util.binary.raw.decode(
    a.toDer(forge.pki.certificateToAsn1(selfSigned(key))).getBytes()
  );

  const body = concat(
    u32(0xfeedfeed),
    u32(2),
    u32(1),
    u32(1),
    utf(alias),
    new Uint8Array(8),
    u32(keyDer.length),
    keyDer,
    u32(1),
    utf("X.509"),
    u32(certDer.length),
    certDer
  );
  return concat(
    body,
    sha1(
      utf16be(storePassword),
      new TextEncoder().encode("Mighty Aphrodite"),
      body
    )
  );
}

describe("newPasswordProblem", () => {
  it("applies upstream's rules", () => {
    expect(newPasswordProblem("short1!", "short1!")).toBe("too_short");
    expect(newPasswordProblem("longenough", "longenougH")).toBe("mismatch");
    expect(newPasswordProblem("has space here", "has space here")).toBe(
      "not_printable_ascii"
    );
    expect(newPasswordProblem("pipe|pipe|", "pipe|pipe|")).toBe(
      "not_printable_ascii"
    );
    expect(newPasswordProblem("påssword1", "påssword1")).toBe(
      "not_printable_ascii"
    );
    expect(newPasswordProblem("Good#pass1", "Good#pass1")).toBeNull();
  });
});

describe("packKeystoreSet", () => {
  const keystore: SigningKeystore = {
    bytes: crypto.getRandomValues(new Uint8Array(2500)),
    storePassword: "store#Pass1",
    alias: "upload",
    keyPassword: "key!Pass2",
  };

  it("round-trips through the workflow's cut and base64 -d", () => {
    const decoded = decodeLikeTheWorkflow(packKeystoreSet(keystore));

    expect(decoded.keystore).toEqual(keystore.bytes);
    expect(decoded.storePassword).toBe("store#Pass1");
    expect(decoded.alias).toBe("upload");
    expect(decoded.keyPassword).toBe("key!Pass2");
  });

  it("refuses a field the workflow would read back differently", () => {
    expect(() =>
      packKeystoreSet({ ...keystore, storePassword: "a|b" })
    ).toThrow();
    expect(() =>
      packKeystoreSet({ ...keystore, keyPassword: "a\nb" })
    ).toThrow();
    expect(() => packKeystoreSet({ ...keystore, alias: "" })).toThrow();
  });
});

describe("createKeystore", () => {
  it("makes a PKCS12 store that opens with the password and holds the key and certificate under key0", async () => {
    const keystore = await createKeystore("Create#Pass1");

    expect(keystore.alias).toBe(NEW_KEY_ALIAS);
    expect(keystoreFormat(keystore.bytes)).toEqual({
      kind: "pkcs12",
      aliases: ["key0"],
    });
    expect(keystore.storePassword).toBe("Create#Pass1");
    expect(keystore.keyPassword).toBe("Create#Pass1");

    const p12 = forge.pkcs12.pkcs12FromAsn1(
      der(keystore.bytes),
      "Create#Pass1"
    );
    const keyBags = p12.getBags({
      friendlyName: NEW_KEY_ALIAS,
      bagType: forge.pki.oids.pkcs8ShroudedKeyBag,
    });
    const certBags = p12.getBags({ bagType: forge.pki.oids.certBag })[
      forge.pki.oids.certBag
    ]!;
    const key = keyBags.friendlyName![0].key as forge.pki.rsa.PrivateKey;
    const cert = certBags[0].cert!;

    expect(key.n.bitLength()).toBe(2048);
    expect(cert.publicKey).toMatchObject({ n: key.n });
    expect(cert.subject.getField("CN").value).toBe("AAPS");
    expect(
      cert.validity.notAfter.getFullYear() -
        cert.validity.notBefore.getFullYear()
    ).toBe(50);
    expect(() =>
      forge.pkcs12.pkcs12FromAsn1(der(keystore.bytes), "Wrong#Pass1")
    ).toThrow(/MAC/);

    const decoded = decodeLikeTheWorkflow(packKeystoreSet(keystore));
    expect(decoded.keystore).toEqual(keystore.bytes);
    expect(decoded.alias).toBe("key0");
  });

  it("refuses a password upstream's rules refuse", async () => {
    await expect(createKeystore("short")).rejects.toThrow();
  });
});

describe("importKeystore", () => {
  it("accepts a PKCS12 file with its password and alias", async () => {
    const bytes = await pkcs12Fixture("p12#Pass", "upload");

    expect(keystoreFormat(bytes)).toEqual({
      kind: "pkcs12",
      aliases: ["upload"],
    });
    const result = await importKeystore(
      bytes,
      "p12#Pass",
      "Upload",
      "p12#Pass"
    );

    if (!result.ok) throw new Error(result.problem);
    expect(result.checked).toBe(true);
    expect(result.keystore).toMatchObject({
      storePassword: "p12#Pass",
      alias: "Upload",
    });
  });

  it("refuses a PKCS12 file with a wrong password or an unknown alias", async () => {
    const bytes = await pkcs12Fixture("p12#Pass", "upload");

    expect(
      await importKeystore(bytes, "wrong#Pass", "upload", "wrong#Pass")
    ).toEqual({
      ok: false,
      problem: "wrong_store_password",
    });
    expect(
      await importKeystore(bytes, "p12#Pass", "other", "p12#Pass")
    ).toEqual({
      ok: false,
      problem: "alias_not_found",
      aliases: ["upload"],
    });
  });

  it("accepts a JKS file with its passwords, keeping the stored alias spelling", async () => {
    // keytool writes JKS aliases in lower case.
    const bytes = await jksFixture("store#Pass", "mykey", "key#Pass");

    expect(keystoreFormat(bytes)).toEqual({ kind: "jks", aliases: ["mykey"] });
    const result = await importKeystore(
      bytes,
      "store#Pass",
      "MyKey",
      "key#Pass"
    );

    if (!result.ok) throw new Error(result.problem);
    expect(result.checked).toBe(true);
    expect(result.keystore.alias).toBe("mykey");
    expect(
      decodeLikeTheWorkflow(packKeystoreSet(result.keystore)).keystore
    ).toEqual(bytes);
  });

  it("refuses a JKS file with a wrong store password, alias or key password", async () => {
    const bytes = await jksFixture("store#Pass", "mykey", "key#Pass");

    expect(
      await importKeystore(bytes, "wrong#Pass", "mykey", "key#Pass")
    ).toEqual({
      ok: false,
      problem: "wrong_store_password",
    });
    expect(
      await importKeystore(bytes, "store#Pass", "other", "key#Pass")
    ).toEqual({
      ok: false,
      problem: "alias_not_found",
      aliases: ["mykey"],
    });
    expect(
      await importKeystore(bytes, "store#Pass", "mykey", "wrong#Pass")
    ).toEqual({
      ok: false,
      problem: "wrong_key_password",
    });
  });

  it("refuses what is not a keystore, empty fields, a separator and an oversized file", async () => {
    const bytes = await pkcs12Fixture("p12#Pass", "upload");

    expect(
      await importKeystore(new TextEncoder().encode("hello"), "a", "b", "c")
    ).toEqual({
      ok: false,
      problem: "not_keystore",
    });
    expect(await importKeystore(bytes, "p12#Pass", " ", "p12#Pass")).toEqual({
      ok: false,
      problem: "empty",
    });
    expect(
      await importKeystore(bytes, "p12|Pass", "upload", "p12|Pass")
    ).toEqual({
      ok: false,
      problem: "contains_pipe",
    });
    const large = new Uint8Array(MAX_KEYSTORE_BYTES + 1);
    large[0] = 0x30;
    expect(await importKeystore(large, "a", "b", "c")).toEqual({
      ok: false,
      problem: "too_large",
    });
  });
});
