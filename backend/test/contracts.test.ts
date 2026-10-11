import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { validatePlan, validatePlaybook, validateUiSnapshot } from "../src/schema";

const root = join(__dirname, "../../docs/contracts/fixtures");
const validators = {
  "ui-snapshot": validateUiSnapshot,
  plan: validatePlan,
  playbook: validatePlaybook,
} as const;

for (const [name, validate] of Object.entries(validators)) {
  for (const kind of ["valid", "invalid"] as const) {
    const dir = join(root, name, kind);
    const files = readdirSync(dir).filter((f) => f.endsWith(".json"));
    describe(`${name} fixtures (${kind})`, () => {
      it("has fixtures", () => expect(files.length).toBeGreaterThan(0));
      for (const file of files) {
        it(`${file} ${kind === "valid" ? "validates" : "is rejected"}`, () => {
          const data = JSON.parse(readFileSync(join(dir, file), "utf8"));
          const ok = validate(data);
          expect(ok, JSON.stringify(validate.errors)).toBe(kind === "valid");
        });
      }
    });
  }
}
