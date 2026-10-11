import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";
import plan from "../../docs/contracts/plan.schema.json";
import playbook from "../../docs/contracts/playbook.schema.json";
import uiSnapshot from "../../docs/contracts/ui-snapshot.schema.json";

const ajv = new Ajv2020({ allErrors: true, strict: true, strictRequired: false });
addFormats(ajv);

export const validatePlan = ajv.compile(plan);
export const validatePlaybook = ajv.compile(playbook);
export const validateUiSnapshot = ajv.compile(uiSnapshot);
