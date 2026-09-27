"use client";

import { useEffect, useState } from "react";
import { api, ResumeVariant, ResumeVariantKind } from "@/lib/api";
import { renderResumeMarkdown } from "@/lib/Markdown";

const KINDS: ResumeVariantKind[] = ["FullStack", "Frontend", "Backend"];

const KIND_LABELS: Record<ResumeVariantKind, string> = {
    FullStack: "Full Stack",
    Frontend: "Frontend",
    Backend: "Backend",
};

const KIND_HINTS: Record<ResumeVariantKind, string> = {
    FullStack: "Used for postings that ask for both frontend and backend work.",
    Frontend: "Used for postings that lean frontend/React-heavy.",
    Backend: "Used for postings that lean backend/API/database-heavy.",
};

type ViewMode = "edit" | "preview";

export default function ResumesPage() {
    const [variants, setVariants] = useState<ResumeVariant[] | null>(null);
    const [activeKind, setActiveKind] = useState<ResumeVariantKind>("FullStack");
    const [draft, setDraft] = useState<string>("");
    const [view, setView] = useState<ViewMode>("edit");
    const [error, setError] = useState<string | null>(null);
    const [saving, setSaving] = useState(false);
    const [savedJustNow, setSavedJustNow] = useState(false);

    useEffect(() => {
        api
            .getResumes()
            .then((rows) => {
                setVariants(rows);
                const active = rows.find((r) => r.kind === activeKind);
                setDraft(active?.markdown ?? "");
            })
            .catch((e) => setError(e instanceof Error ? e.message : String(e)));
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    function switchTo(kind: ResumeVariantKind) {
        setActiveKind(kind);
        setView("edit");
        setSavedJustNow(false);
        const found = variants?.find((r) => r.kind === kind);
        setDraft(found?.markdown ?? "");
    }

    async function handleSave() {
        setSaving(true);
        setError(null);
        setSavedJustNow(false);
        try {
            const updated = await api.updateResume(activeKind, draft);
            setVariants((prev) =>
                prev ? prev.map((r) => (r.kind === activeKind ? updated : r)) : prev
            );
            setSavedJustNow(true);
        } catch (e) {
            setError(e instanceof Error ? e.message : String(e));
        } finally {
            setSaving(false);
        }
    }

    const active = variants?.find((r) => r.kind === activeKind);
    const isDirty = active !== undefined && draft !== active.markdown;

    return (
        <div>
            <div className="mb-8">
                <p className="eyebrow mb-2">Your base resumes</p>
                <h1 className="text-3xl font-bold tracking-tight sm:text-4xl">Resumes</h1>
                <p className="muted mt-2">
                    Paste your own ATS-formatted resume for each variant below — no file editing or restart needed.
                    The next pipeline run tailors from whatever&apos;s saved here.
                </p>
            </div>

            {error && (
                <p className="status-message mb-5 rounded-lg px-4 py-3 text-sm">
                    Error: {error} — is ApplyEngine.Api running, and does NEXT_PUBLIC_API_BASE_URL in .env.local match its
                    port?
                </p>
            )}

            {!variants && !error && <p className="muted">Loading...</p>}

            {variants && (
                <div>
                    <div className="mb-5 flex flex-wrap gap-2">
                        {KINDS.map((k) => {
                            const row = variants.find((r) => r.kind === k);
                            return (
                                <button
                                    key={k}
                                    onClick={() => switchTo(k)}
                                    className={`rounded-lg px-4 py-2 text-sm font-bold ${activeKind === k
                                        ? "bg-[#0f766e] text-white shadow-sm"
                                        : "border border-[#dce7e3] bg-white text-[#667873] hover:border-[#9bcfc3]"
                                        }`}
                                >
                                    {KIND_LABELS[k]}
                                    {row && !row.isCustomized && (
                                        <span className="ml-2 rounded-full bg-black/10 px-1.5 py-0.5 text-[10px] font-semibold">
                                            default
                                        </span>
                                    )}
                                </button>
                            );
                        })}
                    </div>

                    <p className="muted mb-4 text-sm">{KIND_HINTS[activeKind]}</p>

                    <div className="surface rounded-xl p-5">
                        <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
                            <div className="flex overflow-hidden rounded-lg border border-[#cdded9] text-xs">
                                <button
                                    onClick={() => setView("edit")}
                                    className={`px-3 py-1.5 font-semibold ${view === "edit" ? "bg-[#16806f] text-white" : "bg-white text-[#587069]"
                                        }`}
                                >
                                    Edit
                                </button>
                                <button
                                    onClick={() => setView("preview")}
                                    className={`px-3 py-1.5 font-semibold ${view === "preview" ? "bg-[#16806f] text-white" : "bg-white text-[#587069]"
                                        }`}
                                >
                                    Preview
                                </button>
                            </div>

                            <p className="muted text-xs">
                                {active?.isCustomized
                                    ? `Your saved resume${active.updatedAtUtc ? ` — updated ${new Date(active.updatedAtUtc).toLocaleString()}` : ""}`
                                    : "Showing the built-in default — save your own to replace it."}
                            </p>
                        </div>

                        {view === "edit" ? (
                            <textarea
                                value={draft}
                                onChange={(e) => {
                                    setDraft(e.target.value);
                                    setSavedJustNow(false);
                                }}
                                spellCheck={false}
                                placeholder="Paste your resume here as Markdown — # headings, **bold**, and - bullet points all render in the formatted PDF and the dashboard's preview."
                                className="h-[28rem] w-full resize-y rounded-lg border border-[#cdded9] bg-white p-4 font-mono text-sm leading-relaxed focus:border-[#16806f] focus:outline-none"
                            />
                        ) : (
                            <div
                                className="h-[28rem] overflow-y-auto rounded-lg border border-[#cdded9] bg-white p-6 leading-relaxed text-[#26332f] [&_h1]:mb-1 [&_h1]:text-2xl [&_h1]:font-bold [&_h2]:mt-5 [&_h2]:mb-2 [&_h2]:border-b [&_h2]:border-[#cdded9] [&_h2]:pb-1 [&_h2]:text-sm [&_h2]:font-semibold [&_h2]:tracking-wide [&_h2]:text-[#587069] [&_h2]:uppercase [&_h3]:mt-3 [&_h3]:mb-1 [&_h3]:text-sm [&_h3]:font-semibold [&_li]:mb-1 [&_li]:text-sm [&_p]:mb-2 [&_p]:text-sm [&_strong]:font-semibold [&_ul]:mb-2 [&_ul]:ml-5 [&_ul]:list-disc"
                                dangerouslySetInnerHTML={{ __html: renderResumeMarkdown(draft) }}
                            />
                        )}

                        <div className="mt-4 flex items-center gap-3">
                            <button
                                onClick={handleSave}
                                disabled={saving || !isDirty}
                                className="rounded-lg bg-[#0f766e] px-4 py-2.5 text-sm font-bold text-white shadow-sm disabled:cursor-not-allowed disabled:opacity-40"
                            >
                                {saving ? "Saving..." : `Save ${KIND_LABELS[activeKind]} resume`}
                            </button>
                            {savedJustNow && <p className="text-sm font-semibold text-[#16806f]">Saved.</p>}
                            {isDirty && !saving && <p className="muted text-xs">Unsaved changes</p>}
                        </div>
                    </div>
                </div>
            )}
        </div>
    );
}