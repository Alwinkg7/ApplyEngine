"use client";

import { useEffect, useState } from "react";
import { useParams, useRouter } from "next/navigation";
import { api, API_BASE_URL, QueueDetail } from "@/lib/api";
import { diffWords } from "@/lib/diff";
import { renderResumeMarkdown } from "@/lib/Markdown";

type Action = "approve" | "skip" | "requeue";
type ResumeView = "diff" | "formatted";

// Send is deliberately not part of Action/handleAction above — every existing
// action just flips a status via one shared helper on the server, but Send
// hits a completely different endpoint (POST .../send) with its own set of
// server-side refusal reasons (not Approved yet, no apply email found, no
// resume/draft yet, already Sent) — see handleSend below and that endpoint's
// remarks in Program.cs.

export default function QueueDetailPage() {
    const params = useParams<{ id: string }>();
    const router = useRouter();
    const [detail, setDetail] = useState<QueueDetail | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);
    // Defaults to the formatted view — it's the one worth printing/saving as a
    // PDF, and most people opening this panel want "what does this actually
    // look like" before "what changed word-for-word". The diff is still one
    // click away for double-checking the tailoring didn't invent anything.
    const [resumeView, setResumeView] = useState<ResumeView>("formatted");

    async function load() {
        setError(null);
        try {
            setDetail(await api.getQueueItem(params.id));
        } catch (e) {
            setError(e instanceof Error ? e.message : String(e));
        }
    }

    useEffect(() => {
        load();
        // Only re-fetch when the route's id actually changes.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [params.id]);

    async function handleAction(action: Action) {
        setBusy(true);
        setError(null);
        try {
            const fn = action === "approve" ? api.approveMatch : action === "skip" ? api.skipMatch : api.requeueMatch;
            await fn(params.id);
            router.push("/queue");
        } catch (e) {
            setError(e instanceof Error ? e.message : String(e));
            setBusy(false);
        }
    }

    // Irreversible (it hands your resume to your SMTP server), so this gets
    // its own native confirm() on top of the server's own guardrails —
    // cheap, no new dependency, and it's a rare-enough action that a browser
    // confirm dialog isn't an annoyance the way it would be on Approve/Skip.
    async function handleSend() {
        if (!confirm(`Send this application to ${detail?.applyEmail}? This can't be undone.`)) return;

        setBusy(true);
        setError(null);
        try {
            await api.sendMatch(params.id);
            router.push("/queue");
        } catch (e) {
            setError(e instanceof Error ? e.message : String(e));
            setBusy(false);
        }
    }

    // The portal-apply counterpart to handleSend — for the (far more common)
    // case where ApplyEmailExtractor found nothing to email. Nothing is sent
    // by ApplyEngine here; this just records that you went and applied
    // yourself via "Open Application" below.
    async function handleMarkApplied() {
        if (!confirm("Mark this as applied? This assumes you've already submitted the employer's application form yourself.")) return;

        setBusy(true);
        setError(null);
        try {
            await api.markApplied(params.id);
            router.push("/queue");
        } catch (e) {
            setError(e instanceof Error ? e.message : String(e));
            setBusy(false);
        }
    }

    if (error && !detail) return <p className="status-message rounded-lg px-4 py-3 text-sm">Error: {error}</p>;
    if (!detail) return <p className="muted">Loading...</p>;

    const { job } = detail;
    const chips = [
        detail.extractedSalary ? `💰 ${detail.extractedSalary}` : null,
        detail.extractedExperience ? `🗓️ ${detail.extractedExperience}` : null,
        detail.extractedQualification ? `🎓 ${detail.extractedQualification}` : null,
        job.sourceName,
        detail.resumeVariant ? `variant: ${detail.resumeVariant}` : null,
        detail.applyEmail ? `✉️ ${detail.applyEmail}` : null,
    ].filter((c): c is string => c !== null);

    const diffOps =
        detail.baseResumeMarkdown && detail.tailoredResumeMarkdown
            ? diffWords(detail.baseResumeMarkdown, detail.tailoredResumeMarkdown)
            : null;

    const hasResume = !!detail.tailoredResumeMarkdown;

    // Every reason Send could be refused, most specific first — shown as a
    // one-line hint next to the button rather than only surfacing as an error
    // after a failed click, so you know before you try. Mirrors the same
    // checks POST /api/queue/{id}/send enforces server-side; this is just the
    // client-side heads-up, not the real gate.
    const sendBlockedReason = !detail.applyEmail
        ? "No apply email found for this job — most postings are portal-apply, not email-apply."
        : !hasResume || !detail.emailBody
            ? "Resume tailoring or email drafting hasn't finished for this match yet."
            : detail.status !== "Approved"
                ? "Approve this match first."
                : null;

    // Mirrors sendBlockedReason above, for the portal-apply path — only real
    // requirement is Approved, since nothing here depends on the resume or
    // drafted email having finished (there's no email being sent).
    const markAppliedBlockedReason = detail.status !== "Approved" ? "Approve this match first." : null;

    return (
        <div>
            {/* Everything in this print:hidden block is here for reviewing/acting
                on the match on screen — none of it belongs in a PDF of the resume
                itself, so it's hidden from the print stylesheet Chrome/Edge use for
                "Save as PDF" (see the Download PDF button in the Tailored Resume
                section below). */}
            <div className="print:hidden">
                <button onClick={() => router.push("/queue")} className="muted mb-6 text-sm font-semibold hover:text-[#0f766e]">
                    ← Back to Queue
                </button>

                <p className="eyebrow mb-2">Opportunity review</p>
                <h1 className="max-w-3xl text-3xl font-bold tracking-tight sm:text-4xl">{job.title}</h1>
                <p className="muted mb-2 mt-2">
                    {job.company} {job.location ? `· ${job.location}` : ""}
                </p>
                <a href={job.url} target="_blank" rel="noreferrer" className="text-sm font-semibold text-[#0f766e] hover:text-[#d8664a]">
                    Open original posting ↗
                </a>

                <div className="my-4 flex flex-wrap gap-2">
                    {chips.map((c) => (
                        <span key={c} className="rounded-full border border-[#cdded9] bg-white px-3 py-1 text-xs font-semibold text-[#587069]">
                            {c}
                        </span>
                    ))}
                </div>

                {detail.reason && <p className="surface mb-5 rounded-lg border-l-4 border-l-[#d8664a] px-4 py-3 text-sm italic text-[#49645e]">&quot;{detail.reason}&quot;</p>}

                {error && <p className="status-message mb-5 rounded-lg px-4 py-3 text-sm">Error: {error}</p>}

                {detail.status === "Sent" ? (
                    <p className="muted mb-8 text-sm">
                        This match has already been sent{detail.sentAtUtc ? ` (${new Date(detail.sentAtUtc).toLocaleString()})` : ""}
                        {" "}and can&apos;t be changed from here.
                    </p>
                ) : (
                    <div className="mb-8">
                        <div className="flex flex-wrap items-center gap-3">
                            <button
                                onClick={() => handleAction("approve")}
                                disabled={busy || detail.status === "Approved"}
                                className="rounded-lg bg-[#16806f] px-4 py-2.5 text-sm font-bold text-white shadow-sm disabled:opacity-50"
                            >
                                Approve
                            </button>
                            <button
                                onClick={() => handleAction("skip")}
                                disabled={busy || detail.status === "Skipped"}
                                className="rounded-lg border border-[#d58b79] px-4 py-2.5 text-sm font-bold text-[#b84d38] disabled:opacity-50"
                            >
                                Skip
                            </button>
                            {detail.status !== "Queued" && (
                                <button
                                    onClick={() => handleAction("requeue")}
                                    disabled={busy}
                                    className="rounded-lg border border-[#cdded9] px-4 py-2.5 text-sm font-bold text-[#587069] disabled:opacity-50"
                                >
                                    Back to Queue
                                </button>
                            )}
                            {/* Two mutually-exclusive paths depending on whether
                                ApplyEmailExtractor found an address: Send actually
                                emails the resume+draft (Channel A); Mark as Applied is
                                for the far more common portal-apply case, where you
                                open the posting's own application page yourself (the
                                "Open original posting" link above) and just confirm
                                afterward that you went through with it. */}
                            {detail.applyEmail ? (
                                <button
                                    onClick={handleSend}
                                    disabled={busy || !!sendBlockedReason}
                                    title={sendBlockedReason ?? "Send this application by email now"}
                                    className="rounded-lg bg-[#d8664a] px-4 py-2.5 text-sm font-bold text-white shadow-sm disabled:cursor-not-allowed disabled:opacity-40"
                                >
                                    Send Application
                                </button>
                            ) : (
                                <button
                                    onClick={handleMarkApplied}
                                    disabled={busy || !!markAppliedBlockedReason}
                                    title={markAppliedBlockedReason ?? "Record that you applied via the posting's own application page"}
                                    className="rounded-lg bg-[#d8664a] px-4 py-2.5 text-sm font-bold text-white shadow-sm disabled:cursor-not-allowed disabled:opacity-40"
                                >
                                    Mark as Applied
                                </button>
                            )}
                        </div>
                        {detail.applyEmail
                            ? sendBlockedReason && <p className="muted mt-2 text-xs">{sendBlockedReason}</p>
                            : markAppliedBlockedReason && <p className="muted mt-2 text-xs">{markAppliedBlockedReason}</p>}
                    </div>
                )}
            </div>

            <section className="mb-8">
                <div className="mb-2 flex flex-wrap items-center justify-between gap-2 print:hidden">
                    <h2 className="eyebrow">Tailored Resume</h2>
                    {hasResume && (
                        <div className="flex gap-2">
                            <div className="flex overflow-hidden rounded-lg border border-[#cdded9] text-xs">
                                <button
                                    onClick={() => setResumeView("formatted")}
                                    className={`px-3 py-1.5 font-semibold ${resumeView === "formatted" ? "bg-[#16806f] text-white" : "bg-white text-[#587069]"
                                        }`}
                                >
                                    Formatted
                                </button>
                                <button
                                    onClick={() => setResumeView("diff")}
                                    className={`px-3 py-1.5 font-semibold ${resumeView === "diff" ? "bg-[#16806f] text-white" : "bg-white text-[#587069]"
                                        }`}
                                >
                                    Diff
                                </button>
                            </div>
                            {/* GET /api/queue/{id}/resume.pdf renders the same tailored
                                resume server-side with QuestPDF (see ResumePdfRenderer) —
                                the exact PDF Channel A would attach to an application
                                email — so this is a real download rather than routing
                                through the browser's print-to-PDF dialog. */}
                            <a
                                href={`${API_BASE_URL}/api/queue/${params.id}/resume.pdf`}
                                className="rounded-lg border border-[#cdded9] bg-white px-3 py-1.5 text-xs font-semibold text-[#587069] hover:border-[#16806f] hover:text-[#16806f]"
                                title="Downloads the server-rendered resume PDF"
                            >
                                Download PDF
                            </a>
                        </div>
                    )}
                </div>

                {!hasResume && <p className="muted text-sm italic">No tailored resume for this match yet.</p>}

                {/* Diff view: for double-checking what the LLM actually changed —
                    deliberately never printed (print:hidden), since raw diff markup
                    isn't what you'd hand to an employer. */}
                {hasResume && diffOps && (
                    <div className={`print:hidden ${resumeView === "diff" ? "block" : "hidden"}`}>
                        <p className="muted mb-2 text-xs">
                            <span className="rounded bg-green-100 px-1 text-green-700">added</span>{" "}
                            <span className="rounded bg-red-100 px-1 text-red-700 line-through">removed</span> vs. your base
                            &quot;{detail.resumeVariant}&quot; resume
                        </p>
                        <pre className="surface whitespace-pre-wrap wrap-break-word rounded-xl p-4 font-mono text-sm">
                            {diffOps.map((op, i) => {
                                if (op.type === "add")
                                    return (
                                        <span key={i} className="rounded bg-green-100 text-green-700">
                                            {op.text}
                                        </span>
                                    );
                                if (op.type === "del")
                                    return (
                                        <span key={i} className="rounded bg-red-100 text-red-700 line-through">
                                            {op.text}
                                        </span>
                                    );
                                return <span key={i}>{op.text}</span>;
                            })}
                        </pre>
                    </div>
                )}

                {/* Formatted view: a clean, single-column, ATS-friendly rendering of
                    the resume Markdown — no tables/columns/graphics, since those are
                    exactly what trips up applicant-tracking-system parsers. Always
                    shown when printing (print:block), regardless of which tab is
                    selected on screen, so "Download PDF" reliably produces this
                    version and never the diff. */}
                {hasResume && (
                    <div
                        className={`${resumeView === "formatted" ? "block" : "hidden"} surface print:block rounded-xl p-8 leading-relaxed text-[#26332f] print:rounded-none print:border-0 print:p-0 print:shadow-none [&_h1]:mb-1 [&_h1]:text-2xl [&_h1]:font-bold [&_h2]:mt-5 [&_h2]:mb-2 [&_h2]:border-b [&_h2]:border-[#cdded9] [&_h2]:pb-1 [&_h2]:text-sm [&_h2]:font-semibold [&_h2]:tracking-wide [&_h2]:text-[#587069] [&_h2]:uppercase [&_h3]:mt-3 [&_h3]:mb-1 [&_h3]:text-sm [&_h3]:font-semibold [&_li]:mb-1 [&_li]:text-sm [&_p]:mb-2 [&_p]:text-sm [&_strong]:font-semibold [&_ul]:mb-2 [&_ul]:ml-5 [&_ul]:list-disc`}
                        dangerouslySetInnerHTML={{ __html: renderResumeMarkdown(detail.tailoredResumeMarkdown ?? "") }}
                    />
                )}
            </section>

            <section className="print:hidden">
                <h2 className="eyebrow mb-2">
                    Drafted Application Email
                </h2>
                {detail.emailBody ? (
                    <div className="surface rounded-xl p-5">
                        {detail.applyEmail && (
                            <p className="muted mb-1 text-xs">To: {detail.applyEmail}</p>
                        )}
                        <p className="mb-2 font-semibold">{detail.emailSubject}</p>
                        <p className="whitespace-pre-wrap text-sm">{detail.emailBody}</p>
                    </div>
                ) : (
                    <p className="muted text-sm italic">No drafted application email for this match yet.</p>
                )}
            </section>
        </div>
    );
}