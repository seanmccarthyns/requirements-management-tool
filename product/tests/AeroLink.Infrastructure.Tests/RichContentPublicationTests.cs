using System.IO.Compression;
using System.Text;
using AeroLink.Domain.Content;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A generated document is what an approver signs and what an auditor reads years later. What is asserted
/// here is that the tables and figures an author wrote reach that document, and that when one of them cannot
/// be retrieved the document says so rather than quietly omitting it.
/// </summary>
public sealed class RichContentPublicationTests
{
    /// <summary>A two-by-two PNG: red, green on the top row, blue, white below.</summary>
    private static byte[] Png() => Png(2, 2,
    [
        0, 255, 0, 0, 0, 255, 0,
        0, 0, 0, 255, 255, 255, 255,
    ]);

    private static byte[] Png(int width, int height, byte[] raw)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true)) zlib.Write(raw);

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; header[9] = 2; // eight-bit RGB, non-interlaced
        Chunk(output, "IHDR", header.ToArray());
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();

        static void Chunk(Stream target, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            target.Write(length);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            target.Write(body);
            Span<byte> crc = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
            target.Write(crc);
        }

        static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var input in bytes)
            {
                crc ^= input;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
            }
            return ~crc;
        }
    }

    [Fact]
    public void A_png_decodes_to_the_pixels_a_pdf_needs()
    {
        // PDF has no PNG filter, so a controlled PDF has to carry the pixels. Getting this wrong produces a
        // document that opens and shows a scrambled diagram, which is worse than one that shows none.
        Assert.True(PngImage.TryDecodeRgb(Png(), out var width, out var height, out var rgb));
        Assert.Equal(2, width);
        Assert.Equal(2, height);
        Assert.Equal([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255], rgb);
    }

    [Fact]
    public void Dimensions_are_readable_without_decoding_the_image()
    {
        Assert.Equal((2, 2), PngImage.Size(Png()));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF })]
    [InlineData(new byte[] { })]
    public void Something_that_is_not_a_png_is_reported_as_such_rather_than_throwing(byte[] bytes)
    {
        // One unreadable image must not stop a document that is otherwise complete from being produced.
        Assert.False(PngImage.IsPng(bytes));
        Assert.False(PngImage.TryDecodeRgb(bytes, out _, out _, out _));
    }

    [Fact]
    public void A_truncated_png_is_refused_rather_than_half_decoded()
    {
        var truncated = Png()[..30];
        Assert.False(PngImage.IsPng(truncated));
        Assert.False(PngImage.TryDecodeRgb(truncated, out _, out _, out _));
    }

    [Fact]
    public void A_png_requires_a_complete_bounded_chunk_stream_and_decoder_profile()
    {
        byte[] trailing = [.. Png(), (byte)0x7F];
        Assert.False(PngImage.IsDeclaredImage(trailing, "image/png"));

        var missingEnd = Png()[..^12];
        Assert.False(PngImage.IsDeclaredImage(missingEnd, "image/png"));

        var malformedLength = Png();
        // IHDR is the first chunk at byte 8. Claiming more bytes than the bounded payload contains must fail.
        malformedLength[11] = 0x7F;
        Assert.False(PngImage.IsDeclaredImage(malformedLength, "image/png"));

        var badCrc = Png();
        badCrc[29] ^= 0x01; // IHDR CRC; valid bytes must carry a matching chunk checksum.
        Assert.False(PngImage.IsDeclaredImage(badCrc, "image/png"));

        var invalidFilter = Png();
        // The tiny fixture's compressed scanlines begin in the IDAT payload; change the first filter byte to
        // an undefined value after rebuilding the bounded zlib stream so this reaches the decoder gate.
        var invalidFilterRaw = new byte[] { 5, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 255 };
        invalidFilter = Png(2, 2, invalidFilterRaw);
        Assert.False(PngImage.IsDeclaredImage(invalidFilter, "image/png"));

        var hugeDimensions = Png();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hugeDimensions.AsSpan(16, 4), 32_769);
        Assert.False(PngImage.IsDeclaredImage(hugeDimensions, "image/png"));
    }

    [Fact]
    public void A_jpeg_requires_a_structural_frame_scan_and_terminal_eoi()
    {
        var jpeg = MinimalJpeg(); /* legacy fixture retained in source for provenance:
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAH/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAEFAqf/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/AX//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/AX//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAY/Aqf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/Iqf/2gAMAwEAAgADAAAAEP/EABQRAQAAAAAAAAAAAAAAAAAAABD/2gAIAQMBAT8Qf//EABQRAQAAAAAAAAAAAAAAAAAAABD/2gAIAQIBAT8Qf//EABQQAQAAAAAAAAAAAAAAAAAAABD/2gAIAQEAAT8Qf//Z");

        */
        Assert.True(PngImage.IsDeclaredImage(jpeg, "image/jpeg"));
        Assert.False(PngImage.IsDeclaredImage(jpeg[..^2], "image/jpeg"));
        Assert.False(PngImage.IsDeclaredImage([.. jpeg, (byte)0x00], "image/jpeg"));
        Assert.False(PngImage.IsDeclaredImage([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10], "image/jpeg"));
    }

    [Fact]
    public void A_jpeg_rejects_missing_tables_bad_lengths_and_resource_bombs()
    {
        var missingQuantization = MinimalJpeg();
        var frame = Array.IndexOf(missingQuantization, (byte)0xC0);
        missingQuantization[frame + 12] = 1; // frame component selects a table that was not declared
        Assert.False(PngImage.IsDeclaredImage(missingQuantization, "image/jpeg"));

        var missingHuffman = MinimalJpeg();
        var scan = Array.IndexOf(missingHuffman, (byte)0xDA);
        missingHuffman[scan + 6] = 0x10; // AC table 1 was not declared
        Assert.False(PngImage.IsDeclaredImage(missingHuffman, "image/jpeg"));

        var malformedFrameLength = MinimalJpeg();
        frame = Array.IndexOf(malformedFrameLength, (byte)0xC0);
        malformedFrameLength[frame + 3] = 0x0A; // SOF payload is no longer exactly 6 + 3*N bytes
        Assert.False(PngImage.IsDeclaredImage(malformedFrameLength, "image/jpeg"));

        var resourceBomb = MinimalJpeg();
        frame = Array.IndexOf(resourceBomb, (byte)0xC0);
        resourceBomb[frame + 5] = 0xFF;
        resourceBomb[frame + 6] = 0xFF;
        resourceBomb[frame + 7] = 0xFF;
        resourceBomb[frame + 8] = 0xFF;
        Assert.False(PngImage.IsDeclaredImage(resourceBomb, "image/jpeg"));
    }

    private static byte[] MinimalJpeg()
    {
        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        Segment(output, 0xDB, [0x00, .. Enumerable.Repeat((byte)1, 64)]);
        Segment(output, 0xC0, [8, 0, 1, 0, 1, 1, 1, 0x11, 0]);
        Segment(output, 0xC4, [0x00, 1, .. Enumerable.Repeat((byte)0, 15), 0]);
        Segment(output, 0xC4, [0x10, 1, .. Enumerable.Repeat((byte)0, 15), 0]);
        Segment(output, 0xDA, [1, 1, 0, 0, 63, 0]);
        output.Write([0x00, 0xFF, 0xD9]);
        return output.ToArray();

        static void Segment(Stream target, byte marker, byte[] payload)
        {
            target.Write([0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
            target.Write(payload);
        }
    }

    [Fact]
    public void A_png_deflate_bomb_is_refused_before_the_compressed_stream_can_expand_unboundedly()
    {
        // IHDR permits exactly four decompressed bytes (filter + RGB). The tiny ZIP representation expands to
        // megabytes, which used to be copied to an unbounded MemoryStream before that mismatch was checked.
        var bomb = Png(1, 1, Enumerable.Repeat((byte)0, 2 * 1024 * 1024).ToArray());
        Assert.False(PngImage.TryDecodeRgb(bomb, out _, out _, out _));
    }

    [Fact]
    public void A_single_decompressed_byte_beyond_the_declared_scanlines_is_refused()
    {
        // #849 Finding 3's exact boundary: the decoder reads only the four bytes the 1x1 IHDR permits and
        // then reads one more byte. A single byte of decompressed excess — far too small to trip the
        // megabyte bomb above — must refuse the image at that check.
        var oneByteOver = Png(1, 1, [0, 255, 0, 0, 0]);
        Assert.False(PngImage.IsPng(oneByteOver));
        Assert.False(PngImage.TryDecodeRgb(oneByteOver, out _, out _, out _));
    }

    [Fact]
    public void Declared_scanlines_that_decompress_short_are_refused()
    {
        // IHDR declares 2x2 (eight filtered bytes) but the compressed stream yields only the first
        // scanline. The exact-sized buffer can never fill, so the image is refused rather than decoded
        // with rows missing.
        var shortScanlines = Png(2, 2, [0, 255, 0, 0, 0, 255, 0]);
        Assert.False(PngImage.IsPng(shortScanlines));
        Assert.False(PngImage.TryDecodeRgb(shortScanlines, out _, out _, out _));
    }

    [Fact]
    public void Dimensions_over_the_decoded_pixel_ceiling_are_refused_before_allocation()
    {
        // The ceiling check runs while parsing IHDR, before a single pixel buffer exists: a declaration of
        // 10,000,772 pixels (just over the ten-million limit) with a trivially small payload is refused
        // without ever allocating the scanline or RGB buffers the declaration would imply.
        var justOver = Png(4_096, 2_442, []);
        Assert.False(PngImage.IsPng(justOver));
        Assert.False(PngImage.TryDecodeRgb(justOver, out _, out _, out _));
    }

    [Fact]
    public void An_image_at_the_decoded_pixel_ceiling_still_decodes()
    {
        // The boundary is inclusive for honest artifacts: 32,768 x 305 is exactly under the ten-million-
        // pixel limit on both per-side and total caps, and decodes completely.
        const int width = 32_768;
        const int height = 305;
        var raw = new byte[(3 * width + 1) * height]; // per row: one filter byte (0) plus the RGB pixels
        Assert.True(PngImage.TryDecodeRgb(Png(width, height, raw), out var decodedWidth, out var decodedHeight, out var rgb));
        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        Assert.Equal((long)width * height * 3, rgb.Length);
    }

    [Fact]
    public void An_authored_image_reaches_the_document_as_its_bytes()
    {
        var id = Guid.NewGuid();
        var stored = RichContent.Canonicalize(
            $$"""{"blocks":[{"type":"image","attachmentId":"{{id}}","alt":"Bus timing","caption":"Figure 1"}]}""");
        var prepared = RichContentPublisher.ForPublication(stored,
            new Dictionary<Guid, string> { [id] = "data:image/png;base64,AAAA" });

        Assert.Contains("\"dataUri\":\"data:image/png;base64,AAAA\"", prepared);
        Assert.Contains("\"caption\":\"Figure 1\"", prepared);
    }

    [Fact]
    public void An_authored_image_width_reaches_publication_without_becoming_markup()
    {
        var id = Guid.NewGuid();
        var stored = RichContent.Canonicalize(
            $$"""{"blocks":[{"type":"image","attachmentId":"{{id}}","alt":"Bus timing","widthPercent":50}]}""");
        var prepared = RichContentPublisher.ForPublication(stored,
            new Dictionary<Guid, string> { [id] = "data:image/png;base64,AAAA" });

        Assert.Contains("\"widthPercent\":50", prepared);
        Assert.DoesNotContain("<img", prepared);
    }

    [Fact]
    public void An_image_whose_file_is_gone_becomes_visible_text_not_a_silent_gap()
    {
        var stored = RichContent.Canonicalize(
            $$"""{"blocks":[{"type":"image","attachmentId":"{{Guid.NewGuid()}}","alt":"Bus timing","caption":"Figure 1"}]}""");

        // A document with a visible gap is recoverable. A document with an invisible one is not: nobody
        // reading it can tell that a figure the author wrote was ever meant to be there.
        var prepared = RichContentPublisher.ForPublication(stored, new Dictionary<Guid, string>());
        Assert.Contains("Image not retrieved: Figure 1", prepared);
        Assert.DoesNotContain("dataUri", prepared);
    }

    [Fact]
    public void Publication_cap_selects_the_first_authored_images_deterministically()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var third = Guid.NewGuid(); var fourth = Guid.NewGuid(); var fifth = Guid.NewGuid();
        var sizes = new Dictionary<Guid, long>
        {
            [first] = 12 * 1024 * 1024,
            [second] = 12 * 1024 * 1024,
            [third] = 12 * 1024 * 1024,
            [fourth] = 12 * 1024 * 1024,
            [fifth] = 12 * 1024 * 1024,
        };

        // Exactly 48 MiB fits. Reversing database enumeration must not change which authored figures a
        // signed report renders: the ordered block references are the authority.
        Assert.Equal([first, second, third, fourth],
            RichContentPublisher.SelectForPublication([first, second, third, fourth, fifth], sizes));
        Assert.Equal([fifth, fourth, third, second],
            RichContentPublisher.SelectForPublication([fifth, fourth, third, second, first], sizes));
    }

    [Fact]
    public void A_table_reaches_the_document_with_its_rows_intact()
    {
        var stored = RichContent.Canonicalize(
            """{"blocks":[{"type":"table","caption":"Modes","rows":[["Mode","Value"],["Cruise","250"]]}]}""");
        var prepared = RichContentPublisher.ForPublication(stored, new Dictionary<Guid, string>());

        Assert.Contains("\"type\":\"table\"", prepared);
        Assert.Contains("[\"Cruise\",\"250\"]", prepared);
    }

    [Fact]
    public void Content_that_was_never_authored_prepares_to_nothing()
    {
        Assert.Equal(RichContent.Empty, RichContentPublisher.ForPublication(null, new Dictionary<Guid, string>()));
    }

    [Fact]
    public void Docx_preserves_each_adjacent_image_occurrence_while_deduplicating_bytes()
    {
        var uri = "data:image/png;base64," + Convert.ToBase64String(Png());
        var rich = "{\"blocks\":["
            + $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"First alt\",\"caption\":\"First caption\",\"widthPercent\":40}},"
            + $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"Second alt\",\"caption\":\"Second caption\",\"widthPercent\":60}},"
            + "{\"type\":\"paragraph\",\"text\":\"Text below figures.\"}]}";
        var output = ProfessionalPublicationRenderer.Render(Publication(rich), "docx", "inline-images");

        using var zip = new ZipArchive(new MemoryStream(output.Content), ZipArchiveMode.Read);
        var document = Read(zip, "word/document.xml");
        var relationships = Read(zip, "word/_rels/document.xml.rels");
        var media = zip.Entries.Count(entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal));

        Assert.Equal(1, media); // identical bytes are one package asset, not one asset per occurrence
        Assert.Equal(1, relationships.Split("Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\"").Length - 1);
        Assert.Contains("<w:tbl", document); // adjacent figures share one fixed-layout Word row
        Assert.Contains("cx=\"3600000\"", document); // 40% of the row width
        Assert.Contains("cx=\"5400000\"", document); // 60% of the row width
        Assert.True(document.IndexOf("descr=\"First alt\"", StringComparison.Ordinal) < document.IndexOf("First caption", StringComparison.Ordinal));
        Assert.True(document.IndexOf("First caption", StringComparison.Ordinal) < document.IndexOf("descr=\"Second alt\"", StringComparison.Ordinal));
        Assert.Contains("Second caption", document);
        Assert.True(document.IndexOf("Second caption", StringComparison.Ordinal) < document.IndexOf("Text below figures.", StringComparison.Ordinal));

        // The package asset is intentionally shared, but each visual placement must still own a unique
        // wp:docPr and pic:cNvPr ID. Word treats those IDs as drawing identities, not media identities.
        var docPrIds = System.Text.RegularExpressions.Regex.Matches(document, "<wp:docPr id=\"(\\d+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        var cNvPrIds = System.Text.RegularExpressions.Regex.Matches(document, "<pic:cNvPr id=\"(\\d+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(2, docPrIds.Length);
        Assert.Equal(docPrIds.Length, docPrIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, cNvPrIds.Length);
        Assert.Equal(cNvPrIds.Length, cNvPrIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(docPrIds.Order(StringComparer.Ordinal), cNvPrIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Pdf_preserves_each_adjacent_image_occurrence_metadata_and_order()
    {
        var uri = "data:image/png;base64," + Convert.ToBase64String(Png());
        var rich = "{\"blocks\":["
            + $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"First alt\",\"caption\":\"First caption\",\"widthPercent\":40}},"
            + $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"Second alt\",\"caption\":\"Second caption\",\"widthPercent\":60}},"
            + "{\"type\":\"paragraph\",\"text\":\"Text below figures.\"}]}";
        var output = ProfessionalPublicationRenderer.Render(Publication(rich), "pdf", "inline-images");
        var pdf = Encoding.ASCII.GetString(output.Content);

        Assert.Equal(1, Count(pdf, "/Subtype /Image"));
        Assert.Equal(2, Count(pdf, "/Im1 Do"));
        Assert.Contains("187.2 0 0 187.2", pdf); // 40% occurrence in the shared row
        Assert.Contains("280.8 0 0 280.8", pdf); // 60% occurrence in the shared row
        Assert.True(pdf.IndexOf("First caption", StringComparison.Ordinal) < pdf.IndexOf("Second caption", StringComparison.Ordinal));
        Assert.True(pdf.IndexOf("First alt", StringComparison.Ordinal) < pdf.IndexOf("Second alt", StringComparison.Ordinal));
        Assert.True(pdf.IndexOf("Second alt", StringComparison.Ordinal) < pdf.IndexOf("Text below figures.", StringComparison.Ordinal));
    }

    [Fact]
    public void Pdf_preserves_plain_projection_line_breaks_as_separate_text_lines()
    {
        var record = new PublicationRecord("Problem", "Narrative", "Problem statement",
            "First figure description\nSecond figure description\nText below figures.", [], RichContent.Empty);
        var publication = new ProfessionalPublication(
            "FMS", "Flight Management System (FMS)", "FMS Showcase", "Problem Report", "Line break test",
            "Controlled narrative", "PR-00001.00", "00", "Draft", "1.6", "Not yet baseline-effective",
            "test.engineer", new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero), new string('a', 64),
            [], [], [], [new PublicationSection("Narrative", "", [record])]);

        var output = ProfessionalPublicationRenderer.Render(publication, "pdf", "plain-line-breaks");
        var pdf = Encoding.ASCII.GetString(output.Content);

        Assert.Contains("(First figure description) Tj", pdf);
        Assert.Contains("(Second figure description) Tj", pdf);
        Assert.Contains("(Text below figures.) Tj", pdf);
    }

    [Fact]
    public void Pdf_paginates_long_narrative_after_an_image_without_writing_below_the_page()
    {
        var uri = "data:image/png;base64," + Convert.ToBase64String(Png());
        var lines = Enumerable.Range(1, 30).Select(index => $"After-image line {index:D2}.").ToArray();
        var rich = "{\"blocks\":["
            + $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"Long narrative figure\",\"widthPercent\":100}},"
            + $"{{\"type\":\"paragraph\",\"text\":{System.Text.Json.JsonSerializer.Serialize(string.Join("\n", lines))}}}"
            + "]}";

        var output = ProfessionalPublicationRenderer.Render(Publication(rich), "pdf", "inline-image-long-narrative");
        var pdf = Encoding.ASCII.GetString(output.Content);
        var placements = System.Text.RegularExpressions.Regex.Matches(
            pdf,
            @"1 0 0 1 66 (?<y>-?\d+) Tm \(After-image line (?<line>\d{2})\.\) Tj")
            .Select(match => (Y: int.Parse(match.Groups["y"].Value), Line: match.Groups["line"].Value))
            .ToArray();

        Assert.Contains("INLINE IMAGE NARRATIVE - CONTINUED", pdf);
        Assert.Equal(lines.Select((_, index) => (index + 1).ToString("D2")), placements.Select(item => item.Line));
        Assert.All(placements, item => Assert.True(item.Y >= 52, $"Line {item.Line} was emitted below the printable page at y={item.Y}."));
    }

    [Fact]
    public void Pdf_keeps_image_rows_in_global_authored_order_with_surrounding_narrative()
    {
        var uri = "data:image/png;base64," + Convert.ToBase64String(Png());
        var rich = "{\"blocks\":["
            + "{\"type\":\"paragraph\",\"text\":\"Before figures marker.\"},"
            + $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"First ordered alt\",\"caption\":\"First ordered caption\",\"widthPercent\":40}},"
            + $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"Second ordered alt\",\"caption\":\"Second ordered caption\",\"widthPercent\":60}},"
            + "{\"type\":\"paragraph\",\"text\":\"Immediately below figures marker.\"},"
            + "{\"type\":\"paragraph\",\"text\":\"After figures marker.\"}]}";

        var output = ProfessionalPublicationRenderer.Render(Publication(rich), "pdf", "inline-images-global-order");
        var pdf = Encoding.ASCII.GetString(output.Content);
        var before = pdf.IndexOf("Before figures marker.", StringComparison.Ordinal);
        var first = pdf.IndexOf("First ordered caption", StringComparison.Ordinal);
        var second = pdf.IndexOf("Second ordered caption", StringComparison.Ordinal);
        var below = pdf.IndexOf("Immediately below figures marker.", StringComparison.Ordinal);
        var after = pdf.IndexOf("After figures marker.", StringComparison.Ordinal);

        Assert.True(before >= 0 && first > before && second > first && below > second && after > below,
            $"Expected authored narrative/image-row order; positions were before={before}, first={first}, second={second}, below={below}, after={after}.");
        Assert.Equal(1, Count(pdf, "/Subtype /Image"));
        Assert.Equal(2, Count(pdf, "/Im1 Do"));
    }

    public static IEnumerable<object[]> AuthoredImageRows()
    {
        yield return new object[] { new[] { 100, 100 }, new[] { 1, 1 } };
        yield return new object[] { new[] { 60, 60 }, new[] { 1, 1 } };
        yield return new object[] { new[] { 25, 25, 25, 25, 25 }, new[] { 4, 1 } };
        yield return new object[] { new[] { 40, 60, 25 }, new[] { 2, 1 } };
    }

    [Theory]
    [MemberData(nameof(AuthoredImageRows))]
    public void Docx_wraps_overflowing_adjacent_figures_without_rewriting_authored_widths(
        int[] widths, int[] expectedRowSizes)
    {
        var output = ProfessionalPublicationRenderer.Render(Publication(ImageBlocks(widths)), "docx", "inline-image-rows");
        using var zip = new ZipArchive(new MemoryStream(output.Content), ZipArchiveMode.Read);
        var document = Read(zip, "word/document.xml");
        var xml = System.Xml.Linq.XDocument.Parse(document);
        System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        System.Xml.Linq.XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        var imageRows = xml.Descendants(w + "tbl")
            .Where(table => table.Descendants(w + "drawing").Any())
            .ToList();

        Assert.Equal(expectedRowSizes, imageRows.Select(row => row.Descendants(w + "drawing").Count()));
        Assert.Equal(widths.Select(width => 9_000_000L * width / 100),
            imageRows.SelectMany(row => row.Descendants(wp + "extent"))
                .Select(extent => long.Parse(extent.Attribute("cx")!.Value)));
    }

    [Theory]
    [MemberData(nameof(AuthoredImageRows))]
    public void Pdf_wraps_overflowing_adjacent_figures_without_rewriting_authored_widths(
        int[] widths, int[] expectedRowSizes)
    {
        var output = ProfessionalPublicationRenderer.Render(Publication(ImageBlocks(widths)), "pdf", "inline-image-rows");
        var pdf = Encoding.ASCII.GetString(output.Content);

        Assert.Equal(expectedRowSizes.Length, Count(pdf, "CONTROLLED INLINE IMAGE"));
        var offset = 0;
        var expectedOperators = new List<string>();
        foreach (var rowSize in expectedRowSizes)
        {
            var available = 480d - 12d * Math.Max(0, rowSize - 1);
            for (var index = 0; index < rowSize; index++)
            {
                var renderedWidth = available * widths[offset + index] / 100d;
                expectedOperators.Add($"q {renderedWidth:0.###} 0 0");
            }
            offset += rowSize;
        }
        foreach (var expected in expectedOperators.GroupBy(value => value))
            Assert.Equal(expected.Count(), Count(pdf, expected.Key));
    }

    [Fact]
    public void Pdf_limits_total_decoded_png_pixels_and_names_the_omitted_figure()
    {
        // Each 4,000 x 2,000 line-art PNG is highly compressible but decodes to 24 MB RGB. The PDF retains
        // only the first two (16M pixels total); the authored third figure remains a visible placeholder.
        var raw = new byte[(4_000 * 3 + 1) * 2_000];
        var uris = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            raw[^1] = (byte)(index + 1);
            uris.Add("data:image/png;base64," + Convert.ToBase64String(Png(4_000, 2_000, raw)));
        }
        var rich = "{\"blocks\":[" + string.Join(",", uris.Select((uri, index) =>
            $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"Cap {index + 1}\",\"caption\":\"Cap {index + 1}\"}}")) + "]}";

        var output = ProfessionalPublicationRenderer.Render(Publication(rich), "pdf", "inline-images-cap");
        var pdf = Encoding.ASCII.GetString(output.Content);

        Assert.Equal(2, Count(pdf, "/Subtype /Image"));
        Assert.Contains("[Image not retrieved: Cap 3]", pdf);
    }

    private static ProfessionalPublication Publication(string rich) => new(
        "FMS", "Flight Management System (FMS)", "FMS Showcase", "Problem Report", "Inline image test",
        "Controlled narrative", "PR-00001.00", "00", "Draft", "1.6", "Not yet baseline-effective", "test.engineer",
        new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero), new string('a', 64), [], [], [],
        [new PublicationSection("Narrative", "", [new PublicationRecord("Problem", "Narrative", "Problem", "", [], rich)])]);

    private static string ImageBlocks(IEnumerable<int> widths)
    {
        var uri = "data:image/png;base64," + Convert.ToBase64String(Png());
        return "{\"blocks\":[" + string.Join(",", widths.Select((width, index) =>
            $"{{\"type\":\"image\",\"dataUri\":\"{uri}\",\"alt\":\"Figure {index + 1}\",\"caption\":\"Figure {index + 1}\",\"widthPercent\":{width}}}")) + "]}";
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static int Count(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;
}
