"use client";

import { useEffect, useState } from "react";
import { api, ApplicationStats } from "@/lib/api";

// One series per chart here (a single count, or two mutually-exclusive
// counts with their own text labels) — no categorical palette needed, so
// this reuses the app's own two existing brand colors (teal for the
// email-send path, coral for the portal-apply path — same colors those two
// actions already use on the Queue detail page) rather than introducing a
// new one. Both bars carry their own text label, so identity never rests on
// color alone.
const TEAL = "#0f766e";
const CORAL = "#d8664a";

function StatTile({ label, value }: { label: string; value: number }) {
    return (
        <div className="surface rounded-xl p-5">
            <p className="muted text-xs font-semibold uppercase tracking-wide">{label}</p>
            <p className="mt-1 text-3xl font-bold">{value}</p>
        </div>
    );
}

function DailyTimelineChart({ data }: { data: ApplicationStats["dailyTimeline"] }) {
    const max = Math.max(1, ...data.map((d) => d.count));

    return (
        <div className="surface rounded-xl p-5">
            <p className="mb-4 text-sm font-semibold">Applications sent — last 30 days</p>
            <div className="flex h-32 items-end gap-1">
                {data.map((d) => {
                    // 4px minimum so a zero-count day still shows a visible
                    // baseline sliver instead of disappearing entirely — a
                    // day with genuinely nothing sent should still read as
                    // "present, zero", not "missing from the chart".
                    const heightPct = Math.max(4, (d.count / max) * 100);
                    return (
                        <div
                            key={d.date}
                            className="group relative flex-1"
                            title={`${d.date}: ${d.count} application${d.count === 1 ? "" : "s"}`}
                        >
                            <div
                                className="w-full rounded-t-sm bg-[#0f766e] transition-opacity group-hover:opacity-80"
                                style={{ height: `${heightPct}%` }}
                            />
                        </div>
                    );
                })}
            </div>
            <div className="muted mt-2 flex justify-between text-xs">
                <span>{data[0]?.date}</span>
                <span>{data[data.length - 1]?.date}</span>
            </div>
        </div>
    );
}

function RankedBarList({
    title,
    rows,
}: {
    title: string;
    rows: { label: string; count: number }[];
}) {
    const max = Math.max(1, ...rows.map((r) => r.count));

    return (
        <div className="surface rounded-xl p-5">
            <p className="mb-4 text-sm font-semibold">{title}</p>
            {rows.length === 0 ? (
                <p className="muted text-sm italic">Nothing sent yet.</p>
            ) : (
                <ul className="space-y-2.5">
                    {rows.map((r) => (
                        <li key={r.label}>
                            <div className="mb-1 flex items-baseline justify-between gap-2 text-sm">
                                <span className="truncate font-medium">{r.label}</span>
                                <span className="muted shrink-0 text-xs font-semibold">{r.count}</span>
                            </div>
                            <div className="h-2 w-full overflow-hidden rounded-full bg-[#edf4f1]">
                                <div
                                    className="h-full rounded-full bg-[#0f766e]"
                                    style={{ width: `${(r.count / max) * 100}%` }}
                                />
                            </div>
                        </li>
                    ))}
                </ul>
            )}
        </div>
    );
}

function EmailVsPortalSplit({ viaEmail, viaPortal }: { viaEmail: number; viaPortal: number }) {
    const total = Math.max(1, viaEmail + viaPortal);

    return (
        <div className="surface rounded-xl p-5">
            <p className="mb-4 text-sm font-semibold">Email vs. portal-apply</p>
            <div className="flex h-3 w-full overflow-hidden rounded-full bg-[#edf4f1]">
                {viaEmail > 0 && <div style={{ width: `${(viaEmail / total) * 100}%`, backgroundColor: TEAL }} />}
                {viaPortal > 0 && (
                    <div style={{ width: `${(viaPortal / total) * 100}%`, backgroundColor: CORAL }} />
                )}
            </div>
            <div className="mt-3 flex gap-5 text-sm">
                <span className="flex items-center gap-2">
                    <span className="h-2.5 w-2.5 rounded-full" style={{ backgroundColor: TEAL }} />
                    Email — {viaEmail}
                </span>
                <span className="flex items-center gap-2">
                    <span className="h-2.5 w-2.5 rounded-full" style={{ backgroundColor: CORAL }} />
                    Portal-apply — {viaPortal}
                </span>
            </div>
        </div>
    );
}

export default function ApplicationsPage() {
    const [stats, setStats] = useState<ApplicationStats | null>(null);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        api
            .getApplicationStats()
            .then(setStats)
            .catch((e) => setError(e instanceof Error ? e.message : String(e)));
    }, []);

    return (
        <div>
            <div className="mb-8">
                <p className="eyebrow mb-2">Track your progress</p>
                <h1 className="text-3xl font-bold tracking-tight sm:text-4xl">Applications</h1>
                <p className="muted mt-2">
                    Everything marked Sent — whether it went out by email (Channel A) or you applied through the
                    company&apos;s own portal.
                </p>
            </div>

            {error && (
                <p className="status-message mb-5 rounded-lg px-4 py-3 text-sm">
                    Error: {error} — is ApplyEngine.Api running, and does NEXT_PUBLIC_API_BASE_URL in .env.local match its
                    port?
                </p>
            )}

            {!stats && !error && <p className="muted">Loading...</p>}

            {stats && (
                <div className="space-y-6">
                    <div className="grid grid-cols-2 gap-4 sm:grid-cols-3">
                        <StatTile label="Total sent" value={stats.totalSent} />
                        <StatTile label="Last 7 days" value={stats.sentLast7Days} />
                        <StatTile label="Last 30 days" value={stats.sentLast30Days} />
                    </div>

                    <DailyTimelineChart data={stats.dailyTimeline} />

                    <div className="grid gap-4 sm:grid-cols-2">
                        <RankedBarList
                            title="Top companies"
                            rows={stats.byCompany.map((c) => ({ label: c.company, count: c.count }))}
                        />
                        <div className="space-y-4">
                            <EmailVsPortalSplit viaEmail={stats.viaEmail} viaPortal={stats.viaPortal} />
                            <RankedBarList
                                title="By source"
                                rows={stats.bySource.map((s) => ({ label: s.sourceName, count: s.count }))}
                            />
                        </div>
                    </div>
                </div>
            )}
        </div>
    );
}