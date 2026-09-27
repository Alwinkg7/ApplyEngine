"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { api, MatchStatus, QueueItem } from "@/lib/api";

// "ReadyToApply" isn't a real MatchStatus — it's Approved matches filtered
// client-side to the ones with no applyEmail (see ApplyEmailExtractor's
// remarks: most real postings are portal-apply, not email-apply). Kept as a
// tab of its own rather than folded into "Approved" because it needs a
// completely different action (open the posting's own application page,
// then Mark as Applied) instead of the Link-to-detail card every other tab
// uses — see the render branch below.
type Tab = MatchStatus | "ReadyToApply";

const TABS: Tab[] = ["Queued", "Approved", "ReadyToApply", "Skipped"];

const TAB_LABELS: Record<Tab, string> = {
    New: "New",
    Queued: "Queued",
    Approved: "Approved",
    ReadyToApply: "Ready to Apply",
    Skipped: "Skipped",
    Sent: "Sent",
};

function scoreClass(score: number | null): string {
    if (score === null) return "text-[#8a9b96]";
    if (score >= 75) return "text-[#16806f]";
    if (score >= 60) return "text-[#b7791f]";
    return "text-[#c0523d]";
}

export default function QueuePage() {
    const [tab, setTab] = useState<Tab>("Queued");
    const [items, setItems] = useState<QueueItem[] | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [markingId, setMarkingId] = useState<string | null>(null);

    async function load(t: Tab) {
        setItems(null);
        setError(null);
        try {
            // ReadyToApply has no server-side status of its own — it's just
            // Approved, narrowed down here to the ones with nothing to email.
            const status: MatchStatus = t === "ReadyToApply" ? "Approved" : t;
            const rows = await api.getQueue(status);
            setItems(t === "ReadyToApply" ? rows.filter((m) => !m.applyEmail) : rows);
        } catch (e) {
            setError(e instanceof Error ? e.message : String(e));
        }
    }

    useEffect(() => {
        load(tab);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [tab]);

    async function handleMarkApplied(id: string) {
        if (!confirm("Mark this as applied? This assumes you've already submitted the employer's application form yourself.")) return;

        setMarkingId(id);
        setError(null);
        try {
            await api.markApplied(id);
            await load(tab);
        } catch (e) {
            setError(e instanceof Error ? e.message : String(e));
        } finally {
            setMarkingId(null);
        }
    }

    const emptyLabel = tab === "ReadyToApply" ? "ready to apply" : tab.toLowerCase();

    return (
        <div>
            <div className="mb-8">
                <p className="eyebrow mb-2">Decide with confidence</p>
                <h1 className="text-3xl font-bold tracking-tight sm:text-4xl">Review Queue</h1>
                <p className="muted mt-2">Triage the roles that made it through your filters.</p>
            </div>

            <div className="mb-6 flex flex-wrap gap-2">
                {TABS.map((t) => (
                    <button
                        key={t}
                        onClick={() => setTab(t)}
                        className={`rounded-lg px-4 py-2 text-sm font-bold ${tab === t ? "bg-[#0f766e] text-white shadow-sm" : "border border-[#dce7e3] bg-white text-[#667873] hover:border-[#9bcfc3]"
                            }`}
                    >
                        {TAB_LABELS[t]}
                    </button>
                ))}
            </div>

            {tab === "ReadyToApply" && (
                <p className="muted mb-5 text-sm">
                    Approved matches with no email address to send to — these are portal-apply postings. Open each one&apos;s
                    application page yourself, then mark it applied once you&apos;ve submitted it.
                </p>
            )}

            {error && (
                <p className="status-message mb-5 rounded-lg px-4 py-3 text-sm">
                    Error: {error} — is ApplyEngine.Api running, and does NEXT_PUBLIC_API_BASE_URL in .env.local match its
                    port?
                </p>
            )}

            {items === null && !error && <p className="muted">Loading...</p>}
            {items?.length === 0 && <p className="empty-state rounded-lg p-8 text-center">Nothing {emptyLabel} right now.</p>}

            <ul className="space-y-3">
                {items?.map((m) =>
                    tab === "ReadyToApply" ? (
                        <li key={m.id} className="surface rounded-xl p-5">
                            <div className="flex flex-wrap items-start justify-between gap-4">
                                <div>
                                    <Link href={`/queue/${m.id}`} className="font-semibold hover:text-[#0f766e]">
                                        {m.title}
                                    </Link>
                                    <p className="muted mt-1 text-sm">
                                        {m.company} {m.location ? `· ${m.location}` : ""} · {m.sourceName}
                                    </p>
                                </div>
                                {m.fitScore !== null && (
                                    <span className={`shrink-0 text-sm font-bold ${scoreClass(m.fitScore)}`}>{m.fitScore}</span>
                                )}
                            </div>
                            <div className="mt-4 flex flex-wrap gap-3">
                                <a
                                    href={m.url}
                                    target="_blank"
                                    rel="noreferrer"
                                    className="rounded-lg bg-[#0f766e] px-4 py-2 text-sm font-bold text-white shadow-sm hover:bg-[#115e59]"
                                >
                                    Open Application ↗
                                </a>
                                <button
                                    onClick={() => handleMarkApplied(m.id)}
                                    disabled={markingId === m.id}
                                    className="rounded-lg border border-[#cdded9] bg-white px-4 py-2 text-sm font-bold text-[#587069] hover:border-[#16806f] hover:text-[#16806f] disabled:opacity-50"
                                >
                                    {markingId === m.id ? "Marking..." : "Mark as Applied"}
                                </button>
                            </div>
                        </li>
                    ) : (
                        <li key={m.id}>
                            <Link href={`/queue/${m.id}`} className="surface block rounded-xl p-5 hover:-translate-y-0.5 hover:border-[#9bcfc3]">
                                <div className="flex items-start justify-between gap-4">
                                    <div>
                                        <p className="font-semibold">{m.title}</p>
                                        <p className="muted mt-1 text-sm">
                                            {m.company} {m.location ? `· ${m.location}` : ""} · {m.sourceName}
                                        </p>
                                    </div>
                                    {m.fitScore !== null && (
                                        <span className={`shrink-0 text-sm font-bold ${scoreClass(m.fitScore)}`}>{m.fitScore}</span>
                                    )}
                                </div>
                                <div className="mt-4 flex flex-wrap gap-2 text-xs font-semibold text-[#718780]">
                                    {m.hasTailoredResume && <span className="rounded-full bg-[#edf4f1] px-2.5 py-1">Resume ready</span>}
                                    {m.hasEmailDraft && <span className="rounded-full bg-[#fff1eb] px-2.5 py-1 text-[#a8553e]">Draft ready</span>}
                                    {m.applyEmail && <span className="rounded-full bg-[#eef1fc] px-2.5 py-1 text-[#3f57a8]">✉️ Sendable</span>}
                                </div>
                            </Link>
                        </li>
                    )
                )}
            </ul>
        </div>
    );
}