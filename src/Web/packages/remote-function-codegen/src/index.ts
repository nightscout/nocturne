#!/usr/bin/env node
// src/Web/packages/remote-function-codegen/src/index.ts
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'fs';
import { resolve, dirname } from 'path';
import { defaultConfig } from './config.js';
import { parseOpenApiSpec } from './parser.js';
import { generateRemoteFunctions } from './generators/remote-functions.js';
import { generateApiClient } from './generators/api-client.js';

async function main() {
  console.log('Remote Function Generator');
  console.log('=========================\n');

  if (!existsSync(defaultConfig.openApiPath)) {
    console.error(`OpenAPI spec not found: ${defaultConfig.openApiPath}`);
    console.error('Run "aspire run" first to generate the OpenAPI spec.');
    process.exit(1);
  }

  const spec = JSON.parse(readFileSync(defaultConfig.openApiPath, 'utf-8'));
  console.log(`Loaded OpenAPI spec: ${spec.info.title} v${spec.info.version}`);

  const parsed = parseOpenApiSpec(spec);
  console.log(`Found ${parsed.operations.length} annotated operations across ${parsed.tags.length} tags.\n`);

  if (parsed.operations.length === 0) {
    console.log('No operations with remote annotations found.');
    console.log('Add [RemoteQuery] or [RemoteCommand] attributes to controller methods.');
    process.exit(0);
  }

  // Generate remote functions
  console.log('Generating remote functions...');
  const remoteFunctions = generateRemoteFunctions(parsed);

  const remoteFunctionsDir = resolve(defaultConfig.outputDir, defaultConfig.remoteFunctionsOutput);
  mkdirSync(remoteFunctionsDir, { recursive: true });

  for (const [fileName, content] of remoteFunctions) {
    const filePath = resolve(remoteFunctionsDir, fileName);
    writeFileSync(filePath, content, 'utf-8');
    console.log(`  Generated: ${defaultConfig.remoteFunctionsOutput}/${fileName}`);
  }

  // Generate ApiClient (optional - writes to a .generated file)
  console.log('\nGenerating ApiClient...');
  const apiClientContent = generateApiClient(spec);
  const apiClientPath = resolve(defaultConfig.outputDir, defaultConfig.apiClientOutput);
  mkdirSync(dirname(apiClientPath), { recursive: true });
  writeFileSync(apiClientPath, apiClientContent, 'utf-8');
  console.log(`  Generated: ${defaultConfig.apiClientOutput}`);

  console.log('\nDone!');
  console.log('\nNote: The generated ApiClient is a reference. The existing api-client.ts');
  console.log('has custom property names and should be maintained manually.');
}

main().catch(console.error);
