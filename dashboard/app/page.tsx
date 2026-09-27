"use client";

import { useEffect, useState } from "react";
import { api, Match, PipelineResult } from "@/lib/api";

export default function MatchesPage() {
  const [matches, setMatches] = useState<Match[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const [lastRun, setLastRun] = useState<PipelineResult | null>(null);

  async function load() {
    try {
      setError(null);
      const data = await api.getMatches();
      setMatches(data);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  useEffect(() => {
    load();
  }, []);

  async function handleRun() {
    setRunning(true);
    setError(null);
    try {
      // Same JobPipelineService your console app and Task Scheduler already
      // use — this hits your real inbox and sends a real digest email.
      const result = await api.runPipeline();
      setLastRun(result);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setRunning(false);
    }
  }

  return (
    <div>
      <div className="mb-8 flex flex-col items-start justify-between gap-5 sm:flex-row sm:items-end">
        <div>
          <p className="eyebrow mb-2">Your signal, sorted</p>
          <h1 className="text-3xl font-bold tracking-tight sm:text-4xl">Matches</h1>
          <p className="muted mt-2 max-w-lg">Fresh opportunities from your sources, ready for a closer look.</p>
        </div>
        <button
          onClick={handleRun}
          disabled={running}
          className="primary-action rounded-lg px-4 py-2.5 text-sm font-bold text-white disabled:cursor-wait disabled:opacity-50"
        >
          {running ? "Running..." : "Run pipeline now"}
        </button>
      </div>

      {lastRun && (
        <p className="surface mb-5 rounded-lg px-4 py-3 text-sm text-[#49645e]">
          Last run: {lastRun.totalFetched} fetched, {lastRun.totalNew} new, {lastRun.matchCount} matched.
          {lastRun.digestEmailed
            ? " Digest emailed."
            : lastRun.digestOutputPath
              ? ` Digest written to ${lastRun.digestOutputPath}.`
              : ""}
        </p>
      )}

      {error && (
        <p className="status-message mb-5 rounded-lg px-4 py-3 text-sm">
          Error: {error} — is ApplyEngine.Api running (dotnet run --project src\ApplyEngine.Api), and does
          NEXT_PUBLIC_API_BASE_URL in .env.local match its port?
        </p>
      )}

      {matches === null && !error && <p className="muted">Loading...</p>}
      {matches?.length === 0 && <p className="empty-state rounded-lg p-8 text-center">No matches in the last 14 days.</p>}

      <ul className="space-y-3">
        {matches?.map((m) => (
          <li key={m.id} className="surface rounded-xl p-5 hover:-translate-y-0.5 hover:border-[#9bcfc3]">
            <a
              href={m.url}
              target="_blank"
              rel="noreferrer"
              className="font-semibold text-[#145d57] hover:text-[#d8664a]"
            >
              {m.title}
            </a>
            <p className="muted mt-1 text-sm">
              {m.company} {m.location ? `· ${m.location}` : ""} · {m.sourceName}
            </p>
            <p className="mt-3 text-xs text-[#8a9b96]">First seen {new Date(m.firstSeenAtUtc).toLocaleString()}</p>
          </li>
        ))}
      </ul>
    </div>
  );
}