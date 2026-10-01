export interface VendorRequest {
  method: string;
  path: string;
  query: Record<string, string>;
  headers: Record<string, string>;
  body: string;
}

export interface VendorReply {
  status: number;
  body: unknown;
}

export interface Vendor {
  handle(request: VendorRequest): VendorReply | Promise<VendorReply>;
}
