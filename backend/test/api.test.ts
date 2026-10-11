import { readFileSync } from "node:fs";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import app, { type Env } from "../src/index";
import { MODEL } from "../src/planner";
import { SYSTEM_PROMPT } from "../src/prompt";

const fixture = (path: string) =>
  JSON.parse(readFileSync(join(__dirname, "../../docs/contracts/fixtures", path), "utf8"));

const snapshot = fixture("ui-snapshot/valid/settings.json");
const goodPlan = fixture("plan/valid/bluetooth.json");
const refusal = fixture("plan/valid/refusal.json");

function fakeKv() {
  const data = new Map<string, string>();
  return {
    data,
    get: async (k: string) => data.get(k) ?? null,
    put: async (k: string, v: string) => void data.set(k, v),
  } as unknown as KVNamespace & { data: Map<string, string> };
}

let env: Env & { KV: ReturnType<typeof fakeKv> };
let modelCalls: { url: string; headers: Record<string, string>; body: any }[];
let modelReplies: (() => Response)[];

const toolReply = (input: unknown) =>
  new Response(
    JSON.stringify({
      content: [{ type: "tool_use", name: "submit_plan", input }],
      usage: { input_tokens: 100, output_tokens: 20, cache_read_input_tokens: 5, cache_creation_input_tokens: 7 },
    }),
  );

beforeEach(() => {
  env = { KV: fakeKv(), ANTHROPIC_API_KEY: "sk-test-secret" } as typeof env;
  modelCalls = [];
  modelReplies = [() => toolReply(goodPlan)];
  vi.stubGlobal("fetch", async (url: string, init: RequestInit) => {
    modelCalls.push({ url, headers: init.headers as Record<string, string>, body: JSON.parse(init.body as string) });
    return (modelReplies.shift() ?? (() => new Response("{}", { status: 500 })))();
  });
  vi.spyOn(console, "log").mockImplementation(() => {});
});

afterEach(() => vi.unstubAllGlobals());

const call = (path: string, body: unknown, headers: Record<string, string> = {}, e: Env = env) =>
  app.request(path, { method: "POST", body: typeof body === "string" ? body : JSON.stringify(body), headers }, e);

async function register(ip = "1.1.1.1"): Promise<string> {
  const res = await call("/v1/register", { appVersion: "0.1.0" }, { "cf-connecting-ip": ip });
  expect(res.status).toBe(200);
  return ((await res.json()) as { installToken: string }).installToken;
}

const planBody = (over: Record<string, unknown> = {}) => ({
  question: "turn on bluetooth",
  snapshot,
  history: [],
  reason: "new",
  ...over,
});
const auth = (t: string) => ({ authorization: `Bearer ${t}` });

describe("/v1/register", () => {
  it("issues a token", async () => {
    expect(await register()).toMatch(/^[0-9a-f]{64}$/);
  });

  it("allows 5 per IP per day, then 429; other IPs are unaffected", async () => {
    for (let i = 0; i < 5; i++) await register("2.2.2.2");
    const res = await call("/v1/register", { appVersion: "0.1.0" }, { "cf-connecting-ip": "2.2.2.2" });
    expect(res.status).toBe(429);
    await register("3.3.3.3");
  });

  it("rejects a malformed body", async () => {
    expect((await call("/v1/register", { appVersion: 1 })).status).toBe(400);
    expect((await call("/v1/register", "not json")).status).toBe(400);
  });
});

describe("/v1/plan", () => {
  it("returns the validated plan and calls the model as specified", async () => {
    const token = await register();
    const res = await call("/v1/plan", planBody(), auth(token));
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual(goodPlan);

    expect(modelCalls).toHaveLength(1);
    const { url, headers, body } = modelCalls[0];
    expect(url).toBe("https://api.anthropic.com/v1/messages");
    expect(headers["x-api-key"]).toBe("sk-test-secret");
    expect(body.model).toBe(MODEL);
    expect(body.system).toEqual([{ type: "text", text: SYSTEM_PROMPT, cache_control: { type: "ephemeral" } }]);
    expect(body.tools).toHaveLength(1);
    expect(body.tools[0].name).toBe("submit_plan");
    expect(body.tools[0].description).toBe("Submit the step-by-step plan, or a refusal, for the person.");
    expect(body.tools[0].input_schema.required).toContain("refusal");
    expect(body.tool_choice).toEqual({ type: "tool", name: "submit_plan" });
    expect(body.messages[0].content).toContain("<question>\"turn on bluetooth\"</question>");
    expect(body.messages[0].content).toContain("<reason>new</reason>");
  });

  it("passes a refusal through", async () => {
    modelReplies = [() => toolReply(refusal)];
    const token = await register();
    const res = await call("/v1/plan", planBody(), auth(token));
    expect(await res.json()).toEqual(refusal);
  });

  it("puts a valid playbookHint in the user message and rejects a malformed one", async () => {
    const token = await register();
    expect((await call("/v1/plan", planBody({ playbookHint: "change-display-scaling" }), auth(token))).status).toBe(200);
    expect(modelCalls[0].body.messages[0].content).toContain("<hint>");

    for (const bad of ["Bad Hint", "x</hint>", 5, "a".repeat(65)]) {
      expect((await call("/v1/plan", planBody({ playbookHint: bad }), auth(token))).status).toBe(400);
    }
    expect(modelCalls).toHaveLength(1);
  });

  it("needs a known bearer token", async () => {
    expect((await call("/v1/plan", planBody())).status).toBe(401);
    expect((await call("/v1/plan", planBody(), auth("0".repeat(64)))).status).toBe(401);
    expect(modelCalls).toHaveLength(0);
  });

  it("rejects bad bodies", async () => {
    const token = await register();
    for (const bad of [
      planBody({ question: "" }),
      planBody({ question: "x".repeat(501) }),
      planBody({ reason: "whenever" }),
      planBody({ history: Array.from({ length: 21 }, () => ({})) }),
      planBody({ snapshot: { elements: [] } }),
      planBody({ extra: true }),
    ]) {
      expect((await call("/v1/plan", bad, auth(token))).status).toBe(400);
    }
    expect(modelCalls).toHaveLength(0);
  });

  it("accepts the limits exactly", async () => {
    const token = await register();
    const res = await call(
      "/v1/plan",
      planBody({ question: "x".repeat(500), history: Array.from({ length: 20 }, () => ({})) }),
      auth(token),
    );
    expect(res.status).toBe(200);
  });

  it("rejects bodies over 64 KB with 413", async () => {
    const token = await register();
    const big = JSON.stringify(planBody({ history: ["y".repeat(70_000)] }));
    expect((await call("/v1/plan", big, auth(token))).status).toBe(413);
    // Also when Content-Length is absent or understated: the real length is checked.
    expect((await call("/v1/plan", big, { ...auth(token), "content-length": "10" })).status).toBe(413);
    expect(modelCalls).toHaveLength(0);
  });

  it("limits each token to 30 calls a day", async () => {
    const token = await register();
    modelReplies = Array.from({ length: 40 }, () => () => toolReply(goodPlan));
    for (let i = 0; i < 30; i++) expect((await call("/v1/plan", planBody(), auth(token))).status).toBe(200);
    expect((await call("/v1/plan", planBody(), auth(token))).status).toBe(429);
    // A fresh token has its own budget.
    expect((await call("/v1/plan", planBody(), auth(await register("9.9.9.9")))).status).toBe(200);
  });

  it("enforces the global daily cap", async () => {
    const capped = { ...env, GLOBAL_DAILY_LIMIT: "2" } as typeof env;
    modelReplies = Array.from({ length: 5 }, () => () => toolReply(goodPlan));
    const a = await register("4.4.4.4");
    const b = await register("5.5.5.5");
    expect((await call("/v1/plan", planBody(), auth(a), capped)).status).toBe(200);
    expect((await call("/v1/plan", planBody(), auth(b), capped)).status).toBe(200);
    expect((await call("/v1/plan", planBody(), auth(a), capped)).status).toBe(429);
    expect(modelCalls).toHaveLength(2);
  });

  it("retries once after invalid output, then succeeds", async () => {
    modelReplies = [() => toolReply({ intent: "x" }), () => toolReply(goodPlan)];
    const token = await register();
    const res = await call("/v1/plan", planBody(), auth(token));
    expect(res.status).toBe(200);
    expect(modelCalls).toHaveLength(2);
  });

  it("gives up after two invalid outputs with 502 and never leaks the key", async () => {
    modelReplies = [() => toolReply({ intent: "x" }), () => toolReply({ steps: [] })];
    const token = await register();
    const res = await call("/v1/plan", planBody(), auth(token));
    expect(res.status).toBe(502);
    expect(await res.text()).not.toContain("sk-test-secret");
    expect(modelCalls).toHaveLength(2);
  });

  it("does not retry a 4xx from the model", async () => {
    modelReplies = [() => new Response("{}", { status: 400 })];
    const token = await register();
    expect((await call("/v1/plan", planBody(), auth(token))).status).toBe(502);
    expect(modelCalls).toHaveLength(1);
  });

  it("logs metadata only", async () => {
    const log = vi.spyOn(console, "log");
    const token = await register();
    await call("/v1/plan", planBody({ question: "secret question text" }), auth(token));
    const logged = log.mock.calls.map((c) => String(c[0])).join("\n");
    expect(logged).not.toContain("secret question text");
    expect(logged).not.toContain(token);
    expect(logged).toContain('"inputTokens":100');
  });
});

describe("/v1/events", () => {
  const event = { sessionId: "abc-123", outcome: "done", stepsCompleted: 3, llmCalls: 1, playbookId: "find-downloads" };

  it("accepts a valid event with 204", async () => {
    const token = await register();
    expect((await call("/v1/events", event, auth(token))).status).toBe(204);
    expect((await call("/v1/events", { ...event, playbookId: undefined }, auth(token))).status).toBe(204);
  });

  it("rejects extra fields such as question text", async () => {
    const token = await register();
    expect((await call("/v1/events", { ...event, question: "how do I" }, auth(token))).status).toBe(400);
    expect((await call("/v1/events", { ...event, outcome: "banana" }, auth(token))).status).toBe(400);
  });

  it("needs a token", async () => {
    expect((await call("/v1/events", event)).status).toBe(401);
  });
});
