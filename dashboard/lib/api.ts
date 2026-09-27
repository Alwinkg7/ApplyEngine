// Talks to ApplyEngine.Api (see src/ApplyEngine.Api/Program.cs). Base URL is
// an env var, not hardcoded, since the ASP.NET Core dev port (the
// https://localhost:XXXX Swagger opened) can change between machines/runs.
export const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL ?? "https://localhost:7215";

export type Preferences = {
  id: number;
  version: number;
  mustHaveKeywords: string[];
  niceToHaveKeywords: string[];
  locations: string[];
  workModes: string[];
  excludeCompanies: string[];
};

export type Match = {
  id: string;
  title: string;
  company: string;
  location: string | null;
  url: string;
  sourceName: string;
  postedAtUtc: string | null;
  firstSeenAtUtc: string;
};

export type PipelineResult = {
  totalFetched: number;
  totalNew: number;
  matchCount: number;
  digestEmailed: boolean;
  digestOutputPath: string | null;
};

// Mirrors ApplyEngine.Domain.Entities.MatchStatus (Program.cs serializes it
// as its name via JsonStringEnumConverter, not the underlying int).
export type MatchStatus = "New" | "Queued" | "Approved" | "Skipped" | "Sent";

// The review queue's list-view shape — GET /api/queue. Deliberately thinner
// than QueueDetail below (no resume/email text) since this is what renders
// for every row in the list, not just the one you've opened.
export type QueueItem = {
  id: string;
  title: string;
  company: string;
  location: string | null;
  url: string;
  sourceName: string;
  postedAtUtc: string | null;
  firstSeenAtUtc: string;
  fitScore: number | null;
  reason: string | null;
  status: MatchStatus;
  resumeVariant: string | null;
  hasTailoredResume: boolean;
  hasEmailDraft: boolean;
  // Spec §7 Channel A: best-effort address ApplyEmailExtractor found in the
  // job's own text — null for the (expected, common) portal-apply case where
  // there's nothing to email. Non-null doesn't mean "sendable yet" — Send is
  // also gated on Approved status and a completed resume + email draft (see
  // POST /api/queue/{id}/send's own guardrails).
  applyEmail: string | null;
};

// GET /api/queue/{id}'s full shape — includes baseResumeMarkdown alongside
// tailoredResumeMarkdown specifically so the detail page can diff the two
// client-side (see lib/diff.ts) without the API doing any diffing itself.
export type QueueDetail = {
  id: string;
  job: {
    title: string;
    company: string;
    location: string | null;
    url: string;
    sourceName: string;
    experienceText: string | null;
    salaryText: string | null;
    postedAtUtc: string | null;
    firstSeenAtUtc: string;
  };
  fitScore: number | null;
  reason: string | null;
  extractedSalary: string | null;
  extractedExperience: string | null;
  extractedQualification: string | null;
  status: MatchStatus;
  resumeVariant: string | null;
  baseResumeMarkdown: string | null;
  tailoredResumeMarkdown: string | null;
  emailSubject: string | null;
  emailBody: string | null;
  applyEmail: string | null;
  scoredAtUtc: string | null;
  resumeTailoredAtUtc: string | null;
  emailDraftedAtUtc: string | null;
  sentAtUtc: string | null;
};

export type QueueStatusChangeResult = {
  id: string;
  status: MatchStatus;
};

// POST /api/queue/{id}/send's success shape — a superset of
// QueueStatusChangeResult (adds sentAtUtc), kept as its own type since the
// two are semantically different actions even though the server response
// happens to overlap.
export type QueueSendResult = {
  id: string;
  status: MatchStatus;
  sentAtUtc: string;
};

// Mirrors ApplyEngine.Domain.Processing.ResumeVariantKind.
export type ResumeVariantKind = "FullStack" | "Frontend" | "Backend";

// GET /api/resumes / PUT /api/resumes/{kind}'s shape — powers the dashboard's
// Resumes editor. isCustomized is false when this is still the shipped
// embedded/external-file default (nothing saved through the dashboard yet).
export type ResumeVariant = {
  kind: ResumeVariantKind;
  markdown: string;
  updatedAtUtc: string | null;
  isCustomized: boolean;
};

// GET /api/stats/applications's shape — powers the Applications tracker page.
// DailyTimeline is always exactly 30 entries (oldest to newest, zero-filled)
// so the chart can render a fixed-width bar per day without special-casing
// days with nothing sent.
export type ApplicationStats = {
  totalSent: number;
  sentLast7Days: number;
  sentLast30Days: number;
  viaEmail: number;
  viaPortal: number;
  byCompany: { company: string; count: number }[];
  bySource: { sourceName: string; count: number }[];
  dailyTimeline: { date: string; count: number }[];
};

async function handle<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new Error(
      `${res.status} ${res.statusText}${text ? ` — ${text}` : ""}`,
    );
  }
  return res.json() as Promise<T>;
}

export const api = {
  getPreferences: () =>
    fetch(`${API_BASE_URL}/api/preferences`, { cache: "no-store" }).then((r) =>
      handle<Preferences>(r),
    ),

  // Server ignores id/version from the body and bumps Version itself — see
  // PUT /api/preferences in Program.cs — so this can send the full object
  // back without risk of forking the singleton row.
  updatePreferences: (prefs: Preferences) =>
    fetch(`${API_BASE_URL}/api/preferences`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(prefs),
    }).then((r) => handle<Preferences>(r)),

  getMatches: (days = 14, limit = 200) =>
    fetch(`${API_BASE_URL}/api/matches?days=${days}&limit=${limit}`, {
      cache: "no-store",
    }).then((r) => handle<Match[]>(r)),

  runPipeline: () =>
    fetch(`${API_BASE_URL}/api/pipeline/run`, { method: "POST" }).then((r) =>
      handle<PipelineResult>(r),
    ),

  // --- Review queue (Phase 2) ---------------------------------------------

  getQueue: (status: MatchStatus = "Queued") =>
    fetch(`${API_BASE_URL}/api/queue?status=${encodeURIComponent(status)}`, {
      cache: "no-store",
    }).then((r) => handle<QueueItem[]>(r)),

  getQueueItem: (id: string) =>
    fetch(`${API_BASE_URL}/api/queue/${id}`, { cache: "no-store" }).then((r) =>
      handle<QueueDetail>(r),
    ),

  approveMatch: (id: string) =>
    fetch(`${API_BASE_URL}/api/queue/${id}/approve`, { method: "POST" }).then(
      (r) => handle<QueueStatusChangeResult>(r),
    ),

  skipMatch: (id: string) =>
    fetch(`${API_BASE_URL}/api/queue/${id}/skip`, { method: "POST" }).then(
      (r) => handle<QueueStatusChangeResult>(r),
    ),

  requeueMatch: (id: string) =>
    fetch(`${API_BASE_URL}/api/queue/${id}/requeue`, { method: "POST" }).then(
      (r) => handle<QueueStatusChangeResult>(r),
    ),

  // Spec §7 Channel A — actually sends the drafted email (with the tailored
  // resume attached) via your configured SMTP account. Irreversible: the
  // server flips Status to Sent on success. Server-side guardrails (must be
  // Approved, must have an applyEmail/resume/draft, can't already be Sent)
  // are the real enforcement — this just surfaces whatever error message
  // comes back (via handle()) so the UI can show it plainly.
  sendMatch: (id: string) =>
    fetch(`${API_BASE_URL}/api/queue/${id}/send`, { method: "POST" }).then(
      (r) => handle<QueueSendResult>(r),
    ),

  // The portal-apply counterpart to sendMatch — no email involved. You open
  // the job's own application page yourself (see the Queue page's "Ready to
  // Apply" tab and the detail page's "Open Application" button), and once
  // you've actually gone through the employer's form, this just records that
  // you did — same Sent status, same one-way door, but nothing is sent by
  // ApplyEngine itself here.
  markApplied: (id: string) =>
    fetch(`${API_BASE_URL}/api/queue/${id}/mark-applied`, {
      method: "POST",
    }).then((r) => handle<QueueSendResult>(r)),

  getApplicationStats: () =>
    fetch(`${API_BASE_URL}/api/stats/applications`, { cache: "no-store" }).then(
      (r) => handle<ApplicationStats>(r),
    ),

  // --- Resume editor (paste your own ATS resume per variant) ---------------

  getResumes: () =>
    fetch(`${API_BASE_URL}/api/resumes`, { cache: "no-store" }).then((r) =>
      handle<ResumeVariant[]>(r),
    ),

  updateResume: (kind: ResumeVariantKind, markdown: string) =>
    fetch(`${API_BASE_URL}/api/resumes/${kind}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ markdown }),
    }).then((r) => handle<ResumeVariant>(r)),
};
