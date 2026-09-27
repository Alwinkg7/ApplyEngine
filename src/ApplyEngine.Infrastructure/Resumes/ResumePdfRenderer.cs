using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ApplyEngine.Infrastructure.Resumes;

/// <summary>
/// Renders a tailored resume's Markdown (produced by ResumeTailor) into a real PDF, server-side,
/// using QuestPDF (Community License — free for personal/non-commercial use; set
/// QuestPDF.Settings.License = LicenseType.Community once at process startup, see Program.cs).
///
/// Deliberately mirrors dashboard/lib/markdown.ts's parsing rules exactly (same subset: headings
/// #/##/###, **bold** inline spans, *italic* spans, [text](url) links, "-"/"*" bullet lists, plain
/// paragraphs; blank lines separate blocks) so what gets emailed/downloaded as a PDF looks like the
/// same document the dashboard's Queue detail page already renders as HTML — not a general
/// Markdown renderer, and not meant to become one. If ResumeTailor's prompt ever starts producing
/// Markdown features outside that subset (tables, nested lists, etc.), both this file and
/// markdown.ts need updating together or the two views will silently diverge.
///
/// This replaces the earlier text/plain resume attachment (see ApplicationEmailSender's original
/// remarks) and the browser-print-to-PDF-only workflow on the dashboard.
/// </summary>
public static class ResumePdfRenderer
{
    public static byte[] Render(string markdown)
    {
        var blocks = ParseBlocks(markdown);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                // Lato, not Arial: QuestPDF only ships/registers Lato out of the box (bundled via
                // its own QuestPDF.Fonts.Lato dependency). Arial isn't installed on the machine
                // running this API, and Settings.UseSystemFonts is (deliberately) left disabled,
                // so Lato is the one font guaranteed to render correctly wherever this API is
                // actually deployed.
                page.DefaultTextStyle(x => x.FontSize(10).FontFamily(Fonts.Lato));

                page.Content().Column(column =>
                {
                    column.Spacing(6);

                    foreach (var block in blocks)
                    {
                        switch (block.Kind)
                        {
                            case BlockKind.Heading1:
                                column.Item().PaddingTop(block == blocks[0] ? 0 : 8).Text(text =>
                                    ComposeInline(text, block.Text, baseSize: 18, bold: true));
                                break;

                            case BlockKind.Heading2:
                                column.Item().PaddingTop(6).Text(text =>
                                    ComposeInline(text, block.Text, baseSize: 13, bold: true));
                                break;

                            case BlockKind.Heading3:
                                column.Item().PaddingTop(4).Text(text =>
                                    ComposeInline(text, block.Text, baseSize: 11, bold: true));
                                break;

                            case BlockKind.BulletList:
                                foreach (var item in block.Items!)
                                {
                                    column.Item().Row(row =>
                                    {
                                        row.ConstantItem(12).Text("•");
                                        row.RelativeItem().Text(text => ComposeInline(text, item, baseSize: 10, bold: false));
                                    });
                                }
                                break;

                            case BlockKind.Paragraph:
                                column.Item().Text(text => ComposeInline(text, block.Text, baseSize: 10, bold: false));
                                break;
                        }
                    }
                });
            });
        }).GeneratePdf();
    }

    // --- Minimal Markdown parsing, mirroring dashboard/lib/markdown.ts's rules -----------------

    private enum BlockKind { Heading1, Heading2, Heading3, BulletList, Paragraph }

    private sealed class Block
    {
        public BlockKind Kind { get; init; }
        public string Text { get; init; } = "";
        public List<string>? Items { get; init; }
    }

    private static List<Block> ParseBlocks(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<Block>();
        var listBuffer = new List<string>();

        void FlushList()
        {
            if (listBuffer.Count == 0) return;
            blocks.Add(new Block { Kind = BlockKind.BulletList, Items = new List<string>(listBuffer) });
            listBuffer.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.Length == 0)
            {
                FlushList();
                continue;
            }

            var headingMatch = System.Text.RegularExpressions.Regex.Match(line, @"^(#{1,3})\s+(.*)$");
            if (headingMatch.Success)
            {
                FlushList();
                var level = headingMatch.Groups[1].Value.Length;
                var kind = level == 1 ? BlockKind.Heading1 : level == 2 ? BlockKind.Heading2 : BlockKind.Heading3;
                blocks.Add(new Block { Kind = kind, Text = headingMatch.Groups[2].Value });
                continue;
            }

            var bulletMatch = System.Text.RegularExpressions.Regex.Match(line, @"^[-*]\s+(.*)$");
            if (bulletMatch.Success)
            {
                listBuffer.Add(bulletMatch.Groups[1].Value);
                continue;
            }

            FlushList();
            blocks.Add(new Block { Kind = BlockKind.Paragraph, Text = line });
        }

        FlushList();
        return blocks;
    }

    // Splits on **bold**, *italic*, and [text](url) link spans (same regex intent as
    // markdown.ts's inline()) and composes each run as its own QuestPDF TextSpan.
    private static void ComposeInline(TextDescriptor text, string raw, float baseSize, bool bold)
    {
        // Bold ("**...**"), then italic ("*...*"), then links ("[text](url)") in the
        // alternation — same "regex tries alternatives in order" reasoning as before: a
        // "**bold**" pair is claimed by the first branch before the looser single-asterisk
        // italic branch ever sees it. Link syntax uses a completely different character set
        // ([ ] ( )), so it never collides with either.
        var parts = System.Text.RegularExpressions.Regex.Split(raw, @"(\*\*.+?\*\*|\*.+?\*|\[.+?\]\(.+?\))");
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;

            // NOTE: QuestPDF's TextSpanDescriptor.Hyperlink(text, url) API is used here to make
            // resume contact links (LinkedIn/GitHub/portfolio) real clickable links in the PDF,
            // not just visible-looking text. This project's sandbox has no NuGet access (a real
            // `dotnet build` has never run here — see this file's git history), so this specific
            // call is UNVERIFIED against your actual installed QuestPDF version. If it fails to
            // compile, check QuestPDF's docs for the exact Hyperlink method signature on your
            // version and adjust this one call — everything else in this file is unchanged from
            // what you've already built successfully.
            var linkMatch = System.Text.RegularExpressions.Regex.Match(part, @"^\[(.+)\]\((.+)\)$");
            if (linkMatch.Success)
            {
                var linkSpan = text.Hyperlink(linkMatch.Groups[1].Value, linkMatch.Groups[2].Value);
                linkSpan.FontSize(baseSize);
                if (bold) linkSpan.Bold();
                continue;
            }

            var boldMatch = System.Text.RegularExpressions.Regex.Match(part, @"^\*\*(.+)\*\*$");
            var italicMatch = boldMatch.Success ? null : System.Text.RegularExpressions.Regex.Match(part, @"^\*(.+)\*$");
            var displayText = boldMatch.Success ? boldMatch.Groups[1].Value
                : italicMatch is { Success: true } ? italicMatch.Groups[1].Value
                : part;

            var span = text.Span(displayText);
            span.FontSize(baseSize);
            if (bold || boldMatch.Success) span.Bold();
            if (italicMatch is { Success: true }) span.Italic();
        }
    }
}