using System.Text;
using System.Text.Json;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DemoCaseGenerator;

/// <summary>
/// Writes the synthetic demo case: text PDFs plus expected.json. Uses fixed values and a fixed seed only (no clock).
/// </summary>
public static class Generator
{
    public const string CaseId = "DEMO-CASE-01";
    public const int Seed = 1801;
    public const string Notice = "Synthetic fictional data only. No document is a real record.";

    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "evaluation")
        {
            EvaluationCaseGenerator.WriteFixture(args[1], int.Parse(args[2]));
            return 0;
        }

        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: DemoCaseGenerator <output-dir>");
            return 2;
        }

        Generate(args[0]);
        return 0;
    }

    public static void Generate(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var docs = Documents();

        foreach (var doc in docs)
        {
            var bytes = BuildPdf(doc.Title, doc.Lines);
            bytes = doc.Damage switch
            {
                Damage.Corrupt => Corrupt(bytes),
                Damage.Truncated => bytes[..(bytes.Length * 6 / 10)],
                _ => bytes,
            };
            File.WriteAllBytes(Path.Combine(outDir, doc.FileName), bytes);
        }

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var documentLines = docs.Select(d => "    " + JsonSerializer.Serialize(
            new ExpectedDocument(d.Id, d.FileName, d.Type, d.Situation, d.Shows, d.Accepted, d.RejectionReason, d.Finding, d.PolicyAction),
            options));
        var json = "{\n"
            + $"  \"caseId\": {JsonSerializer.Serialize(CaseId)},\n"
            + $"  \"seed\": {Seed},\n"
            + $"  \"notice\": {JsonSerializer.Serialize(Notice)},\n"
            + "  \"documents\": [\n"
            + string.Join(",\n", documentLines) + "\n"
            + "  ]\n}\n";
        File.WriteAllText(Path.Combine(outDir, "expected.json"), json, new UTF8Encoding(false));
    }

    private enum Damage
    {
        None,
        Corrupt,
        Truncated,
    }

    private sealed record DemoDocument(
        string Id,
        string FileName,
        string Type,
        string Title,
        string Situation,
        string Shows,
        IReadOnlyList<string> Lines,
        Damage Damage,
        bool Accepted,
        string? RejectionReason,
        string? Finding,
        string PolicyAction);

    private static List<DemoDocument> Documents()
    {
        const string aircraftRelease = "Certificate of release to service (aircraft)";
        const string componentRelease = "Component release certificate";
        const string aircraftTitle = "Release to service: DEMO-AC-01";
        string[] aircraftLines =
        [
            "Aircraft: DEMO-AC-01   Registration: SYN-001",
            "Type: Demo narrowbody (synthetic)   Operator: Example Demo Air (synthetic)",
            "Approved station: Synthetic MRO Station S1   Approval: APP-SYN-000",
            "Release date: 2025-03-01   Work order: WO-SYN-1001",
            "Scope: scheduled A-check, no open defects",
            "Certifying staff: Demo Certifier C1 (synthetic)",
            "Required history: last A-check 2025-03-01, total time 12450 h, total cycles 6120",
            "Release statement: aircraft released to service.",
        ];

        return
        [
            new("DOC-01", "DOC-01-release-aircraft-baseline.pdf", aircraftRelease, aircraftTitle, "baseline-complete",
                "Complete aircraft release with every required field",
                aircraftLines, Damage.None, true, null, null, "ingest-and-link-aircraft"),
            new("DOC-02", "DOC-02-shop-visit-engine-baseline.pdf", "Engine shop-visit report",
                "Shop visit: engine EN-SYN-7781", "baseline-complete",
                "Complete engine record with time and cycle history",
                [
                    "Engine ESN: EN-SYN-7781   Model: DEMO-ENG-A",
                    "Installed on: DEMO-AC-01 (position 1)",
                    "Shop visit: SV-SYN-2201   Date: 2024-11-20",
                    "Time since new: 18200 h   Cycles since new: 9400",
                    "Time since overhaul: 2400 h   Cycles since overhaul: 1300",
                    "Life-limited parts reviewed: yes, none beyond limit",
                    "Release statement: engine released to service.",
                ],
                Damage.None, true, null, null, "ingest-and-link-engine"),
            new("DOC-03", "DOC-03-release-component-a-baseline.pdf", componentRelease,
                "Release: fuel pump CMP-A-0001", "baseline-complete",
                "Complete component release with back-to-birth history attached",
                [
                    "Component: Fuel pump   P/N: FP-SYN-100   S/N: CMP-A-0001",
                    "Fitted to: EN-SYN-7781 (accessory pad 3)",
                    "Release date: 2025-01-15   Approval: APP-SYN-000",
                    "Back-to-birth history: present, 4 prior records attached in package",
                    "Time since new: 5210 h   Cycles since new: 2870",
                    "Condition: serviceable",
                    "Release statement: component released to service.",
                ],
                Damage.None, true, null, null, "ingest-and-link-component"),
            new("DOC-04", "DOC-04-release-component-b-missing-history.pdf", componentRelease,
                "Release: actuator CMP-B-0002", "missing-required-history",
                "Component release that lacks the required back-to-birth history",
                [
                    "Component: Hydraulic actuator   P/N: HA-SYN-200   S/N: CMP-B-0002",
                    "Fitted to: DEMO-AC-01 (position 2)",
                    "Release date: 2025-02-10   Approval: APP-SYN-000",
                    "Time since new: 3980 h   Cycles since new: 2110",
                    "Condition: serviceable",
                    "Back-to-birth history: NOT PROVIDED",
                    "Release statement: component released to service.",
                ],
                Damage.None, false, "REQUIRED_HISTORY_MISSING: back-to-birth history absent", null,
                "reject-and-request-history"),
            new("DOC-05", "DOC-05-shop-visit-engine-ambiguous-id.pdf", "Engine shop-visit report",
                "Shop visit: engine EN-SYN-7718", "ambiguous-identity",
                "ESN one digit away from DOC-02's engine, with identical hours and cycles",
                [
                    "Engine ESN: EN-SYN-7718   Model: DEMO-ENG-A",
                    "Installed on: DEMO-AC-01 (position 1, per work order)",
                    "Shop visit: SV-SYN-2215   Date: 2025-02-02",
                    "Time since new: 18200 h   Cycles since new: 9400",
                    "Note: ESN recorded by hand on the work card; please verify digits.",
                ],
                Damage.None, false, null,
                "AMBIGUOUS_IDENTITY: ESN EN-SYN-7718 is one digit from EN-SYN-7781 with identical hours and cycles",
                "hold-for-human-identity-check"),
            new("DOC-06", "DOC-06-corrupt-release-aircraft.pdf", aircraftRelease, aircraftTitle, "corrupt-file",
                "Copy of DOC-01 with a damaged header and seeded byte flips",
                aircraftLines, Damage.Corrupt, false, "CORRUPT_FILE: header and cross-reference damaged", null,
                "reject-and-request-resubmission"),
            new("DOC-07", "DOC-07-truncated-release-aircraft.pdf", aircraftRelease, aircraftTitle, "truncated-file",
                "Copy of DOC-01 cut off at 60 percent, so the end-of-file marker is missing",
                aircraftLines, Damage.Truncated, false, "TRUNCATED_FILE: end-of-file marker missing", null,
                "reject-and-request-resubmission"),
            new("DOC-08", "DOC-08-contradictory-component-b-overhaul.pdf", "Component overhaul record",
                "Overhaul: actuator CMP-B-0002", "contradictory-later-document",
                "Later record whose time since new is lower than DOC-04's",
                [
                    "Component: Hydraulic actuator   P/N: HA-SYN-200   S/N: CMP-B-0002",
                    "Overhaul date: 2025-03-02   Approval: APP-SYN-000",
                    "Time since new as at 2025-03-02: 3650 h   Cycles since new: 2190",
                ],
                Damage.None, false, null,
                "CONTRADICTION: TSN 3650 h on 2025-03-02 is lower than TSN 3980 h on 2025-02-10 (DOC-04); TSN cannot decrease",
                "hold-and-flag-for-review; DOC-04 is not overwritten"),
            new("DOC-09", "DOC-09-partner-ack-component-a.pdf", "Partner acknowledgement (mock partner)",
                "Partner acknowledgement: CMP-A-0001", "partner-linked-response",
                "Mock partner reply that links back to DOC-03 by serial number",
                [
                    "From: Mock Partner Ltd (synthetic)   Reference: MP-LINK-0001",
                    "Acknowledges receipt of component record for S/N: CMP-A-0001",
                    "Fitted to engine ESN: EN-SYN-7781",
                    "Partner status: confirmed, no conflicts found",
                    "Date: 2025-01-20",
                ],
                Damage.None, true, null,
                "LINKED: partner reference MP-LINK-0001 matches CMP-A-0001 (DOC-03)",
                "link-partner-response-to-DOC-03"),
        ];
    }

    /// <summary>Builds a plain text PDF with PdfPig's builder. Output depends only on the arguments.</summary>
    private static byte[] BuildPdf(string title, IReadOnlyList<string> lines)
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var page = builder.AddPage(595, 842);

        page.AddText("SYNTHETIC DEMO DOCUMENT - FICTIONAL DATA ONLY", 9, new PdfPoint(50, 820), regular);
        page.AddText(title, 14, new PdfPoint(50, 790), bold);
        var y = 760.0;
        foreach (var line in lines)
        {
            page.AddText(line, 11, new PdfPoint(50, y), regular);
            y -= 18;
        }

        return FixTrailerId(builder.Build());
    }

    /// <summary>PdfPig writes a random trailer /ID per build; pin it to fixed same-length values.</summary>
    private static byte[] FixTrailerId(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            "/ID \\[ ?<[0-9A-Fa-f]+><[0-9A-Fa-f]+> ?\\]",
            "/ID [<00000000000000000000000000000000><00000000000000000000000000000000>]");
        return Encoding.Latin1.GetBytes(text);
    }

    /// <summary>Replaces the header and flips a fixed set of seeded body bytes.</summary>
    private static byte[] Corrupt(byte[] source)
    {
        var bytes = (byte[])source.Clone();
        Encoding.ASCII.GetBytes("%QQQ-").CopyTo(bytes, 0);
        var rnd = new Random(Seed);
        for (var i = 0; i < 16; i++)
        {
            var position = rnd.Next(64, bytes.Length - 64);
            bytes[position] ^= 0x5A;
        }

        return bytes;
    }

    public sealed record ExpectedDocument(
        string Id,
        string File,
        string Type,
        string Situation,
        string Shows,
        bool Accepted,
        string? RejectionReason,
        string? Finding,
        string PolicyAction);
}
