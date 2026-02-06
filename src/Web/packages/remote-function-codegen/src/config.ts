import { resolve, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));

export interface GeneratorConfig {
  openApiPath: string;
  schemasPath: string;
  outputDir: string;
  apiClientOutput: string;
  remoteFunctionsOutput: string;
}

export const defaultConfig: GeneratorConfig = {
  openApiPath: resolve(__dirname, '../../app/src/lib/api/generated/openapi.json'),
  schemasPath: resolve(__dirname, '../../app/src/lib/api/generated/schemas.ts'),
  outputDir: resolve(__dirname, '../../app/src/lib'),
  apiClientOutput: 'api/api-client.generated.ts',
  remoteFunctionsOutput: 'data/generated',
};
