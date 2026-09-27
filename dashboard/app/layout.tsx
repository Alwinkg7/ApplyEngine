import type { Metadata } from "next";
import Link from "next/link";
import "./globals.css";

export const metadata: Metadata = {
  title: "ApplyEngine",
  description: "Personal job search dashboard",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body className="app-shell min-h-screen">
        {/* print:hidden on the whole nav, not just one link inside it — this is
            what was still bleeding onto page 2+ of a printed/saved resume PDF
            (see the queue detail page's Download PDF button). The nav, the
            ApplyEngine logo, and all three links only ever belong on screen. */}
        <nav className="app-nav sticky top-0 z-10 border-b px-5 py-4 sm:px-8 print:hidden">
          <div className="mx-auto flex max-w-6xl items-center justify-between gap-5">
            <Link href="/" className="nav-mark text-lg font-bold">
              Apply<span className="text-[#d8664a]">Engine</span>
            </Link>
            <div className="flex gap-5 text-sm font-semibold sm:gap-7">
              <Link href="/" className="nav-link">Matches</Link>
              <Link href="/queue" className="nav-link">Queue</Link>
              <Link href="/applications" className="nav-link">Applications</Link>
              <Link href="/resumes" className="nav-link">Resumes</Link>
              <Link href="/preferences" className="nav-link">Preferences</Link>
            </div>
          </div>
        </nav>
        <main className="mx-auto max-w-6xl px-5 py-9 sm:px-8 sm:py-12">{children}</main>
      </body>
    </html>
  );
}