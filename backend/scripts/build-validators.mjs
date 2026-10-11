// Workers disallow `new Function`, so Ajv can't compile schemas at runtime.
// Generate standalone validator code at build time instead.
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import standaloneCode from "ajv/dist/standalone/index.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const contracts = join(root, "..", "docs", "contracts");
const outDir = join(root, "src", "generated");

const load = (file) => JSON.parse(readFileSync(join(contracts, file), "utf8"));

const ajv = new Ajv2020({
  allErrors: true,
  strict: true,
  strictRequired: false,
  code: { source: true, esm: true },
});

// plan must be registered before playbook, which $refs it by $id.
const plan = load("plan.schema.json");
const playbook = load("playbook.schema.json");
const uiSnapshot = load("ui-snapshot.schema.json");
ajv.addSchema(plan);
ajv.addSchema(playbook);
ajv.addSchema(uiSnapshot);

const code = standaloneCode(ajv, {
  validatePlan: plan.$id,
  validatePlaybook: playbook.$id,
  validateUiSnapshot: uiSnapshot.$id,
});

mkdirSync(outDir, { recursive: true });
writeFileSync(join(outDir, "validators.js"), code);
writeFileSync(
  join(outDir, "validators.d.ts"),
  `import type { ErrorObject } from "ajv";

export interface Validator {
  (data: unknown): boolean;
  errors?: ErrorObject[] | null;
}

export const validatePlan: Validator;
export const validatePlaybook: Validator;
export const validateUiSnapshot: Validator;
`,
);
// The plan schema doubles as the Anthropic tool input_schema, so the two cannot drift.
const { $schema, $id, ...planToolSchema } = plan;
writeFileSync(join(outDir, "plan-tool-schema.json"), JSON.stringify(planToolSchema, null, 2));
console.log("Generated src/generated/validators.js and plan-tool-schema.json");
