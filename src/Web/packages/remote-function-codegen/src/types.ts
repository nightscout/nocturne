export type RemoteType = 'query' | 'command';

export interface ParameterInfo {
  name: string;
  in: 'query' | 'path' | 'header';
  required: boolean;
  type: string;
  enumName?: string;
}

export interface OperationInfo {
  operationId: string;
  tag: string;
  method: string;
  path: string;
  remoteType: RemoteType;
  invalidates: string[];
  parameters: ParameterInfo[];
  requestBodySchema?: string;
  requestBodyRequired?: boolean;
  isArrayBody?: boolean;
  responseSchema?: string;
  isVoidResponse: boolean;
  summary?: string;
  clientPropertyName?: string;
}

export interface ParsedSpec {
  operations: OperationInfo[];
  tags: string[];
}
