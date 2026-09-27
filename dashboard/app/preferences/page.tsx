"use client";

import { useEffect, useState } from "react";
import { api, Preferences } from "@/lib/api";

const LIST_FIELDS: { key: keyof Omit<Preferences, "id" | "version">; label: string; hint: string }[] = [
    { key: "mustHaveKeywords", label: "Must-have keywords", hint: "A job must match at least one of these." },
    { key: "niceToHaveKeywords", label: "Nice-to-have keywords", hint: "Scored later — not used to reject yet." },
    { key: "locations", label: "Locations", hint: '"Remote" always passes regardless of this list.' },
    { key: "workModes", label: "Work modes", hint: "e.g. Remote, Hybrid, Onsite." },
    { key: "excludeCompanies", label: "Exclude companies", hint: "Always rejected, regardless of fit." },
];

export default function PreferencesPage() {
    const [prefs, setPrefs] = useState<Preferences | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [saving, setSaving] = useState(false);
    const [saved, setSaved] = useState(false);

    useEffect(() => {
        api
            .getPreferences()
            .then(setPrefs)
            .catch((e) => setError(e instanceof Error ? e.message : String(e)));
    }, []);

    function updateField(key: keyof Preferences, text: string) {
        if (!prefs) return;
        // Comma-separated text box, split back into a string[] — matches how
        // MustHaveKeywords etc. are stored (List<string> in Preferences.cs).
        const list = text
            .split(",")
            .map((s) => s.trim())
            .filter((s) => s.length > 0);
        setPrefs({ ...prefs, [key]: list });
        setSaved(false);
    }

    async function handleSave() {
        if (!prefs) return;
        setSaving(true);
        setError(null);
        try {
            const updated = await api.updatePreferences(prefs);
            setPrefs(updated);
            setSaved(true);
        } catch (e) {
            setError(e instanceof Error ? e.message : String(e));
        } finally {
            setSaving(false);
        }
    }

    if (error && !prefs) return <p className="status-message rounded-lg px-4 py-3 text-sm">Error: {error}</p>;
    if (!prefs) return <p className="muted">Loading...</p>;

    return (
        <div>
            <div className="mb-8">
                <p className="eyebrow mb-2">Tune your signal</p>
                <h1 className="text-3xl font-bold tracking-tight sm:text-4xl">Preferences</h1>
                <p className="muted mt-2 text-sm">Version {prefs.version} · comma-separated values.</p>
            </div>

            <div className="surface space-y-6 rounded-xl p-5 sm:p-7">
                {LIST_FIELDS.map(({ key, label, hint }) => (
                    <div key={key}>
                        <label className="mb-2 block text-sm font-bold text-[#27443e]">{label}</label>
                        <input
                            type="text"
                            className="w-full rounded-lg border border-[#cdded9] bg-[#fbfdfc] px-3 py-2.5 text-[#172321] shadow-inner shadow-[#edf4f1] placeholder:text-[#8a9b96]"
                            value={prefs[key].join(", ")}
                            onChange={(e) => updateField(key, e.target.value)}
                        />
                        <p className="muted mt-1.5 text-xs">{hint}</p>
                    </div>
                ))}
            </div>

            {error && <p className="status-message mt-4 rounded-lg px-4 py-3 text-sm">Error: {error}</p>}
            {saved && <p className="mt-4 rounded-lg bg-[#e8f5ef] px-4 py-3 text-sm font-semibold text-[#17715f]">Saved.</p>}

            <button
                onClick={handleSave}
                disabled={saving}
                className="primary-action mt-6 rounded-lg px-4 py-2.5 text-sm font-bold text-white disabled:opacity-50"
            >
                {saving ? "Saving..." : "Save preferences"}
            </button>
        </div>
    );
}