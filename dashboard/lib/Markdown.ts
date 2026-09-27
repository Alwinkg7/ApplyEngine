// Deliberately minimal — just enough of Markdown to render what
// ResumeTailor's LLM prompt actually produces (headings, **bold**, *italic*,
// bullet lists, plain paragraphs) as clean HTML for the Queue detail page's
// formatted resume preview. Not a general Markdown parser: no tables,
// images, code blocks, links, or nested lists — a resume doesn't need them,
// and keeping this small means no new npm dependency for something this
// narrow.
//
// All raw text is escaped before any HTML is introduced, so nothing in a
// job posting or resume (both of which can contain arbitrary text from a
// scraped page or an LLM) can inject markup into the page.

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function inline(s: string): string {
  // **bold** first (ResumeTailor's most common inline style) — this consumes
  // every "**...**" pair before the italic pass below ever sees them, so a
  // bold span's own asterisks can never get mistaken for an italic marker.
  // *italic* second, over whatever single asterisks are left — this is what
  // renders things like "*Company Name, City*" under a job title instead of
  // leaving the literal asterisks in the text (previously a known gap: the
  // PDF and this HTML view both showed "*Voleergo Solutions LLP*" verbatim).
  // Runs after escaping, so the literal "*" characters survive escapeHtml
  // untouched and only get turned into real tags here.
  return escapeHtml(s)
    .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
    .replace(/\*(.+?)\*/g, "<em>$1</em>");
}

export function renderResumeMarkdown(markdown: string): string {
  const lines = markdown.replace(/\r\n/g, "\n").split("\n");
  const html: string[] = [];
  let listBuffer: string[] = [];

  function flushList() {
    if (listBuffer.length === 0) return;
    html.push(
      "<ul>" +
        listBuffer.map((item) => `<li>${inline(item)}</li>`).join("") +
        "</ul>",
    );
    listBuffer = [];
  }

  for (const rawLine of lines) {
    const line = rawLine.trim();

    if (line.length === 0) {
      flushList();
      continue;
    }

    const heading = line.match(/^(#{1,3})\s+(.*)$/);
    if (heading) {
      flushList();
      const level = heading[1].length;
      html.push(`<h${level}>${inline(heading[2])}</h${level}>`);
      continue;
    }

    const bullet = line.match(/^[-*]\s+(.*)$/);
    if (bullet) {
      listBuffer.push(bullet[1]);
      continue;
    }

    flushList();
    html.push(`<p>${inline(line)}</p>`);
  }
  flushList();

  return html.join("\n");
}
