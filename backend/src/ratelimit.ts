const TWO_DAYS = 2 * 24 * 60 * 60;

export const utcDay = (now: Date = new Date()) => now.toISOString().slice(0, 10);

/**
 * Counts one use against `limit` for `key`. Returns false (without counting) when the limit is reached.
 * KV has no atomic increment, so concurrent calls can overshoot slightly; fine for a soft cap.
 */
export async function takeOne(kv: KVNamespace, key: string, limit: number): Promise<boolean> {
  const used = Number((await kv.get(key)) ?? "0");
  if (used >= limit) return false;
  await kv.put(key, String(used + 1), { expirationTtl: TWO_DAYS });
  return true;
}

export function limitFrom(value: string | undefined, fallback: number): number {
  const n = Number(value);
  return Number.isFinite(n) && n > 0 ? Math.floor(n) : fallback;
}
