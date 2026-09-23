declare global {
  interface ObjectConstructor {
    keys<T>(obj: T): (keyof T)[];
    values<T>(obj: T): T[keyof T][];
    entries<T>(obj: T): {
      [K in keyof T]-?: [
        K,
        T[K] extends undefined ? undefined : Exclude<T[K], undefined>,
      ];
    }[keyof T][];
  }
  function isNaN(value?: string | number): boolean;

  interface Array<T> {
    map<U>(
      cb: (value: T, i: number, self: T[]) => U,
      thisArg?: any
    ): { [K in keyof this]: U };
  }

  interface String {
    startsWith<T extends string>(prefix: T): this is `${T}${string}`;
  }

  function parseInt(string: number, radix?: number): number;

  const io: typeof import("socket.io-client");
}

export {};
