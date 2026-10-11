import planToolSchema from "./generated/plan-tool-schema.json";
import { buildUserMessage, SYSTEM_PROMPT, type PlannerInput } from "./prompt";
import { validatePlan } from "./schema";

export const MODEL = "claude-haiku-5-5";
export const TOOL_NAME = "submit_plan";

const TOOL = {
  name: TOOL_NAME,
  description: "Submit the step-by-step plan, or a refusal, for the person.",
  input_schema: planToolSchema,
};

export interface Usage {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
}

export type PlannerOutcome = { ok: true; plan: unknown; usage: Usage } | { ok: false; error: string; usage: Usage };

type Fetch = (input: string, init: RequestInit) => Promise<Response>;

const emptyUsage = (): Usage => ({ inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0 });

/** Calls the model with the plan tool forced. Invalid output (or a 5xx/429/network failure) is retried once. */
export async function callPlanner(
  apiKey: string,
  input: PlannerInput,
  fetchImpl: Fetch = fetch,
): Promise<PlannerOutcome> {
  const usage = emptyUsage();
  const body = JSON.stringify({
    model: MODEL,
    max_tokens: 2000,
    system: [{ type: "text", text: SYSTEM_PROMPT, cache_control: { type: "ephemeral" } }],
    tools: [TOOL],
    tool_choice: { type: "tool", name: TOOL_NAME },
    messages: [{ role: "user", content: buildUserMessage(input) }],
  });

  let error = "planner failed";
  for (let attempt = 0; attempt < 2; attempt++) {
    let res: Response;
    try {
      res = await fetchImpl("https://api.anthropic.com/v1/messages", {
        method: "POST",
        headers: { "content-type": "application/json", "x-api-key": apiKey, "anthropic-version": "2023-06-01" },
        body,
      });
    } catch {
      error = "model unreachable";
      continue;
    }

    if (!res.ok) {
      error = `model returned ${res.status}`;
      if (res.status >= 500 || res.status === 429) continue;
      break;
    }

    const data = (await res.json().catch(() => null)) as {
      content?: { type: string; name?: string; input?: unknown }[];
      usage?: Record<string, number>;
    } | null;
    usage.inputTokens += data?.usage?.input_tokens ?? 0;
    usage.outputTokens += data?.usage?.output_tokens ?? 0;
    usage.cacheReadTokens += data?.usage?.cache_read_input_tokens ?? 0;
    usage.cacheWriteTokens += data?.usage?.cache_creation_input_tokens ?? 0;

    const plan = data?.content?.find((b) => b.type === "tool_use" && b.name === TOOL_NAME)?.input;
    if (plan !== undefined && validatePlan(plan)) return { ok: true, plan, usage };
    error = "model returned an invalid plan";
  }
  return { ok: false, error, usage };
}
