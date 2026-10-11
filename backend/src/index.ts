import { Hono } from "hono";
import { callPlanner } from "./planner";
import { limitFrom, takeOne, utcDay } from "./ratelimit";
import { validateUiSnapshot } from "./schema";

export interface Env {
  KV: KVNamespace;
  ANTHROPIC_API_KEY: string;
  DAILY_LIMIT?: string;
  GLOBAL_DAILY_LIMIT?: string;
}

const MAX_BODY_BYTES = 64 * 1024;
const MAX_QUESTION_CHARS = 500;
const MAX_HISTORY = 20;
const REGISTER_PER_IP_PER_DAY = 5;

const REASONS = ["new", "offPlan", "stuck"];
const OUTCOMES = ["done", "gaveUp", "cancelled"];

const app = new Hono<{ Bindings: Env }>();

app.get("/health", (c) => c.json({ status: "ok" }));

type Parsed = { ok: true; body: Record<string, unknown> } | { ok: false; status: 400 | 413 };

async function readJson(req: Request): Promise<Parsed> {
  const declared = Number(req.headers.get("content-length") ?? "0");
  if (declared > MAX_BODY_BYTES) return { ok: false, status: 413 };
  const buffer = await req.arrayBuffer();
  if (buffer.byteLength > MAX_BODY_BYTES) return { ok: false, status: 413 };
  try {
    const body = JSON.parse(new TextDecoder().decode(buffer));
    if (body && typeof body === "object" && !Array.isArray(body)) return { ok: true, body };
  } catch {
    // fall through
  }
  return { ok: false, status: 400 };
}

const onlyKeys = (body: Record<string, unknown>, allowed: string[]) =>
  Object.keys(body).every((k) => allowed.includes(k));

async function authenticate(c: { req: { header(name: string): string | undefined }; env: Env }): Promise<string | null> {
  const match = /^Bearer ([0-9a-f]{64})$/.exec(c.req.header("authorization") ?? "");
  if (!match) return null;
  return (await c.env.KV.get(`token:${match[1]}`)) ? match[1] : null;
}

app.post("/v1/register", async (c) => {
  const parsed = await readJson(c.req.raw);
  if (!parsed.ok) return c.json({ error: "bad request" }, parsed.status);
  const { body } = parsed;
  if (!onlyKeys(body, ["appVersion"]) || typeof body.appVersion !== "string" || body.appVersion.length > 32) {
    return c.json({ error: "bad request" }, 400);
  }

  const ip = c.req.header("cf-connecting-ip") ?? "unknown";
  if (!(await takeOne(c.env.KV, `rl:reg:${ip}:${utcDay()}`, REGISTER_PER_IP_PER_DAY))) {
    return c.json({ error: "too many registrations today" }, 429);
  }

  const bytes = crypto.getRandomValues(new Uint8Array(32));
  const installToken = [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
  await c.env.KV.put(`token:${installToken}`, body.appVersion || "1");
  return c.json({ installToken });
});

app.post("/v1/plan", async (c) => {
  const token = await authenticate(c);
  if (!token) return c.json({ error: "unauthorized" }, 401);

  const parsed = await readJson(c.req.raw);
  if (!parsed.ok) return c.json({ error: "bad request" }, parsed.status);
  const { body } = parsed;

  const { question, reason, history, snapshot, playbookHint } = body;
  if (
    !onlyKeys(body, ["question", "snapshot", "history", "reason", "playbookHint"]) ||
    (playbookHint !== undefined && (typeof playbookHint !== "string" || !/^[a-z0-9]+(-[a-z0-9]+)*$/.test(playbookHint) || playbookHint.length > 64)) ||
    typeof question !== "string" || question.trim().length === 0 || question.length > MAX_QUESTION_CHARS ||
    typeof reason !== "string" || !REASONS.includes(reason) ||
    !Array.isArray(history) || history.length > MAX_HISTORY ||
    !validateUiSnapshot(snapshot)
  ) {
    return c.json({ error: "bad request" }, 400);
  }

  const day = utcDay();
  if (!(await takeOne(c.env.KV, `rl:${token}:${day}`, limitFrom(c.env.DAILY_LIMIT, 30)))) {
    return c.json({ error: "daily limit reached" }, 429);
  }
  if (!(await takeOne(c.env.KV, `rl:global:${day}`, limitFrom(c.env.GLOBAL_DAILY_LIMIT, 1000)))) {
    return c.json({ error: "service is busy today" }, 429);
  }

  const started = Date.now();
  const result = await callPlanner(c.env.ANTHROPIC_API_KEY, {
    question,
    reason: reason as "new" | "offPlan" | "stuck",
    history,
    snapshot,
    playbookHint: playbookHint as string | undefined,
  });
  // Metadata only: no question, snapshot or plan text is logged.
  console.log(JSON.stringify({ route: "plan", token: token.slice(0, 8), ok: result.ok, ms: Date.now() - started, ...result.usage }));

  if (!result.ok) return c.json({ error: "planner unavailable" }, 502);
  return c.json(result.plan);
});

app.post("/v1/events", async (c) => {
  if (!(await authenticate(c))) return c.json({ error: "unauthorized" }, 401);

  const parsed = await readJson(c.req.raw);
  if (!parsed.ok) return c.json({ error: "bad request" }, parsed.status);
  const { body } = parsed;

  const { sessionId, outcome, stepsCompleted, llmCalls, playbookId } = body;
  const count = (v: unknown) => Number.isInteger(v) && (v as number) >= 0 && (v as number) <= 1000;
  if (
    !onlyKeys(body, ["sessionId", "outcome", "stepsCompleted", "llmCalls", "playbookId"]) ||
    typeof sessionId !== "string" || !/^[A-Za-z0-9-]{1,64}$/.test(sessionId) ||
    typeof outcome !== "string" || !OUTCOMES.includes(outcome) ||
    !count(stepsCompleted) || !count(llmCalls) ||
    (playbookId !== undefined && (typeof playbookId !== "string" || !/^[a-z0-9-]{1,64}$/.test(playbookId)))
  ) {
    return c.json({ error: "bad request" }, 400);
  }

  console.log(JSON.stringify({ route: "event", sessionId, outcome, stepsCompleted, llmCalls, playbookId }));
  return c.body(null, 204);
});

export default app;
