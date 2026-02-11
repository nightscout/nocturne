import { describe, it, expect } from 'vitest';
import { parseOpenApiSpec } from '../parser.js';
import type { OpenAPIV3 } from 'openapi-types';

function createSpec(overrides: Partial<OpenAPIV3.Document> = {}): OpenAPIV3.Document {
  return {
    openapi: '3.0.0',
    info: { title: 'Test', version: '1.0.0' },
    paths: {},
    ...overrides,
  };
}

describe('parseOpenApiSpec', () => {
  it('parses operations with remote annotations', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers': {
          get: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_GetDefinitions',
            'x-remote-type': 'query',
            parameters: [],
            responses: {
              '200': {
                description: 'Success',
                content: {
                  'application/json': {
                    schema: { type: 'array', items: { $ref: '#/components/schemas/TrackerDto' } },
                  },
                },
              },
            },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations).toHaveLength(1);
    expect(result.operations[0].operationId).toBe('Trackers_GetDefinitions');
    expect(result.operations[0].remoteType).toBe('query');
    expect(result.operations[0].tag).toBe('V4 Trackers');
  });

  it('skips operations without remote annotations', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers': {
          get: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_GetDefinitions',
            parameters: [],
            responses: { '200': { description: 'OK' } },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations).toHaveLength(0);
  });

  it('detects enum parameters via $ref through oneOf', () => {
    const spec = createSpec({
      components: {
        schemas: {
          TrackerCategory: {
            type: 'string',
            enum: ['Consumable', 'Reservoir', 'Appointment'],
          } as any,
        },
      },
      paths: {
        '/api/v4/trackers': {
          get: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_GetDefinitions',
            'x-remote-type': 'query',
            parameters: [
              {
                name: 'category',
                in: 'query',
                schema: {
                  oneOf: [
                    { nullable: true, oneOf: [{ $ref: '#/components/schemas/TrackerCategory' }] },
                  ],
                },
              },
            ],
            responses: { '200': { description: 'OK' } },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].parameters[0].enumName).toBe('TrackerCategory');
    expect(result.operations[0].parameters[0].type).toBe('string');
  });

  it('detects enum parameters via direct $ref', () => {
    const spec = createSpec({
      components: {
        schemas: {
          TrackerCategory: {
            type: 'string',
            enum: ['Consumable', 'Reservoir'],
          } as any,
        },
      },
      paths: {
        '/api/v4/trackers': {
          get: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_GetDefinitions',
            'x-remote-type': 'query',
            parameters: [
              {
                name: 'category',
                in: 'query',
                schema: { $ref: '#/components/schemas/TrackerCategory' },
              },
            ],
            responses: { '200': { description: 'OK' } },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].parameters[0].enumName).toBe('TrackerCategory');
  });

  it('detects void response for 204 command endpoints', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers/{id}': {
          delete: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_DeleteDefinition',
            'x-remote-type': 'command',
            'x-remote-invalidates': ['GetDefinitions'],
            parameters: [{ name: 'id', in: 'path', required: true, schema: { type: 'string' } }],
            responses: { '204': { description: 'No content' } },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].isVoidResponse).toBe(true);
  });

  it('detects void response for commands with 200 but no JSON body', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers/{id}': {
          delete: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_DeleteDefinition',
            'x-remote-type': 'command',
            parameters: [{ name: 'id', in: 'path', required: true, schema: { type: 'string' } }],
            responses: { '200': { description: '' } },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].isVoidResponse).toBe(true);
  });

  it('does not mark as void when command 200 has JSON content', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers': {
          post: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_Create',
            'x-remote-type': 'command',
            parameters: [],
            requestBody: {
              content: { 'application/json': { schema: { $ref: '#/components/schemas/Request' } } },
            },
            responses: {
              '200': {
                description: 'OK',
                content: { 'application/json': { schema: { $ref: '#/components/schemas/TrackerDto' } } },
              },
            },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].isVoidResponse).toBe(false);
  });

  it('does not mark queries as void even without response schema', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers': {
          get: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_GetDefinitions',
            'x-remote-type': 'query',
            parameters: [],
            responses: { '200': { description: 'OK' } },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].isVoidResponse).toBe(false);
  });

  it('parses 201 response schema for create endpoints', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers': {
          post: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_Create',
            'x-remote-type': 'command',
            parameters: [],
            requestBody: {
              content: { 'application/json': { schema: { $ref: '#/components/schemas/Request' } } },
            },
            responses: {
              '201': {
                description: 'Created',
                content: { 'application/json': { schema: { $ref: '#/components/schemas/TrackerDto' } } },
              },
            },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].responseSchema).toBe('TrackerDto');
    expect(result.operations[0].isVoidResponse).toBe(false);
  });

  it('parses array response types', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers': {
          get: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_GetDefinitions',
            'x-remote-type': 'query',
            parameters: [],
            responses: {
              '200': {
                description: 'OK',
                content: {
                  'application/json': {
                    schema: { type: 'array', items: { $ref: '#/components/schemas/TrackerDefinitionDto' } },
                  },
                },
              },
            },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].responseSchema).toBe('TrackerDefinitionDto[]');
  });

  it('parses single-object response types', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers/{id}': {
          get: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_GetDefinition',
            'x-remote-type': 'query',
            parameters: [{ name: 'id', in: 'path', required: true, schema: { type: 'string' } }],
            responses: {
              '200': {
                description: 'OK',
                content: { 'application/json': { schema: { $ref: '#/components/schemas/TrackerDefinitionDto' } } },
              },
            },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].responseSchema).toBe('TrackerDefinitionDto');
  });

  it('parses request body schema', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers': {
          post: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_Create',
            'x-remote-type': 'command',
            parameters: [],
            requestBody: {
              content: {
                'application/json': {
                  schema: { $ref: '#/components/schemas/CreateTrackerDefinitionRequest' },
                },
              },
            },
            responses: {
              '200': {
                description: 'OK',
                content: { 'application/json': { schema: { $ref: '#/components/schemas/TrackerDto' } } },
              },
            },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].requestBodySchema).toBe('CreateTrackerDefinitionRequestSchema');
  });

  it('parses invalidation targets', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers': {
          post: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_Create',
            'x-remote-type': 'command',
            'x-remote-invalidates': ['GetDefinitions', 'Trackers_GetActiveInstances'],
            parameters: [],
            requestBody: {
              content: { 'application/json': { schema: { $ref: '#/components/schemas/Request' } } },
            },
            responses: { '200': { description: 'OK', content: { 'application/json': { schema: { $ref: '#/components/schemas/Dto' } } } } },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].invalidates).toEqual(['GetDefinitions', 'Trackers_GetActiveInstances']);
  });

  it('parses array request body schema', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/treatments/bulk': {
          post: {
            tags: ['V4 Treatments'],
            operationId: 'Treatments_CreateTreatments',
            'x-remote-type': 'command',
            'x-remote-invalidates': ['GetTreatments'],
            parameters: [],
            requestBody: {
              content: {
                'application/json': {
                  schema: {
                    type: 'array',
                    items: { $ref: '#/components/schemas/Treatment' },
                  },
                },
              },
            },
            responses: {
              '201': {
                description: 'Created',
                content: {
                  'application/json': {
                    schema: { type: 'array', items: { $ref: '#/components/schemas/Treatment' } },
                  },
                },
              },
            },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].requestBodySchema).toBe('TreatmentSchema');
    expect(result.operations[0].isArrayBody).toBe(true);
  });

  it('sets isArrayBody to false for non-array request bodies', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers': {
          post: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_Create',
            'x-remote-type': 'command',
            parameters: [],
            requestBody: {
              content: {
                'application/json': {
                  schema: { $ref: '#/components/schemas/CreateTrackerDefinitionRequest' },
                },
              },
            },
            responses: {
              '200': {
                description: 'OK',
                content: { 'application/json': { schema: { $ref: '#/components/schemas/TrackerDto' } } },
              },
            },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].requestBodySchema).toBe('CreateTrackerDefinitionRequestSchema');
    expect(result.operations[0].isArrayBody).toBe(false);
  });

  it('parses x-client-property from operations', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/foods': {
          get: {
            tags: ['V4 Foods'],
            operationId: 'Foods_GetFavorites',
            'x-remote-type': 'query',
            'x-client-property': 'foodsV4',
            parameters: [],
            responses: { '200': { description: 'OK' } },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].clientPropertyName).toBe('foodsV4');
  });

  it('sets clientPropertyName to undefined when x-client-property is absent', () => {
    const spec = createSpec({
      paths: {
        '/api/v4/trackers': {
          get: {
            tags: ['V4 Trackers'],
            operationId: 'Trackers_GetDefinitions',
            'x-remote-type': 'query',
            parameters: [],
            responses: { '200': { description: 'OK' } },
          } as any,
        },
      },
    });

    const result = parseOpenApiSpec(spec);
    expect(result.operations[0].clientPropertyName).toBeUndefined();
  });

  it('does not include schemas property in ParsedSpec', () => {
    const spec = createSpec({
      components: { schemas: { Foo: { type: 'object' } as any } },
      paths: {},
    });

    const result = parseOpenApiSpec(spec);
    expect(result).not.toHaveProperty('schemas');
  });
});
