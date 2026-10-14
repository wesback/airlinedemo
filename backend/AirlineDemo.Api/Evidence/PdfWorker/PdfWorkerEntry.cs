using System.Text.Json;

namespace AirlineDemo.Api.Evidence.PdfWorker;

public sealed record PdfWorkerResponse(string State, string Reason, string Text);

/// <summary>
/// Child-process entry point. Reads one PDF from standard input and writes a JSON <see cref="PdfWorkerResponse"/>
/// to standard output. The working-set check here is a second guard alongside the parent's supervision.
/// </summary>
public static class PdfWorkerEntry
{
    public const string Switch = "--pdf-worker";

    public static int Run()
    {
        using var stdin = Console.OpenStandardInput();
        var response = Execute(stdin, WithinMemoryLimit);
        using var stdout = Console.OpenStandardOutput();
        JsonSerializer.Serialize(stdout, response);
        stdout.Flush();
        return 0;
    }

    internal static PdfWorkerResponse Execute(Stream stdin, Func<bool> withinMemoryLimit)
    {
        try
        {
            using var input = new MemoryStream();
            stdin.CopyTo(input);

            var outcome = EvidenceIntake.ExtractText(input.ToArray(), withinMemoryLimit);
            return new PdfWorkerResponse(outcome.State.ToString(), outcome.Reason, outcome.Text);
        }
        catch (OutOfMemoryException)
        {
            return new PdfWorkerResponse(
                EvidenceProcessingState.RejectedRetryable.ToString(),
                "exceeded the memory limit; retry is possible",
                string.Empty);
        }
        catch (Exception)
        {
            return new PdfWorkerResponse(
                EvidenceProcessingState.RejectedRetryable.ToString(),
                "worker failed to read the document; retry is possible",
                string.Empty);
        }
    }

    private static bool WithinMemoryLimit() =>
        Environment.WorkingSet <= IsolatedPdfParserOptions.Default.MemoryLimitBytes;
}
