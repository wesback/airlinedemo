using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Evidence.PdfWorker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class EvidenceEndpointsTests
{
    private const string Passcode = "test-passcode";
    private const string Case = "case-001";
    private const long MiB = 1024 * 1024;

    private sealed record Session(string Cookie, string Csrf);

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Passcode"] = Passcode }));
            // The test host runs as root on Linux, so the default parser cannot isolate its worker; point it at this test assembly.
            builder.ConfigureTestServices(services => services.AddSingleton(new IsolatedPdfParserOptions
            {
                WorkerFileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                WorkerArguments = new[] { typeof(IsolatedPdfParser).Assembly.Location, PdfWorkerEntry.Switch },
            }));
        });

    private static async Task<Session> SignInAsync(HttpClient client, string userId, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Session(cookie, body.GetProperty("csrfToken").GetString()!);
    }

    private static byte[] ReadFixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Fixtures", "PdfCorpus")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return File.ReadAllBytes(Path.Combine(dir!, "Fixtures", "PdfCorpus", name));
    }

    private static byte[] FakePdf(long size)
    {
        var bytes = new byte[size];
        Encoding.ASCII.GetBytes("%PDF-").CopyTo(bytes, 0);
        return bytes;
    }

    private static Task<HttpResponseMessage> PostPackageAsync(
        HttpClient client, Session? session, string caseId, IEnumerable<(string Name, byte[] Bytes)> files, bool withCsrf = true)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(part, "files", name);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/cases/{caseId}/packages") { Content = form };
        if (session is not null) request.Headers.Add("Cookie", session.Cookie);
        if (session is not null && withCsrf) request.Headers.Add("X-CSRF-Token", session.Csrf);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetPackageAsync(HttpClient client, Session session, string caseId, string packageId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/cases/{caseId}/packages/{packageId}");
        request.Headers.Add("Cookie", session.Cookie);
        return client.SendAsync(request);
    }

    private static EvidenceBasisStore Basis(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<EvidenceBasisStore>();

    [Fact]
    public async Task Presenter_submits_valid_package_and_reads_back_its_state_and_results()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter", CancellationToken.None);

        using var created = await PostPackageAsync(client, presenter, Case, new[] { ("statement.pdf", ReadFixture("valid-text.pdf")) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var package = await created.Content.ReadFromJsonAsync<JsonElement>();
        var packageId = package.GetProperty("packageId").GetString()!;
        Assert.Equal("complete", package.GetProperty("state").GetString());
        Assert.Equal("accepted", package.GetProperty("documents")[0].GetProperty("status").GetString());

        using var fetched = await GetPackageAsync(client, presenter, Case, packageId);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var read = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("complete", read.GetProperty("state").GetString());
        Assert.Equal("statement.pdf", read.GetProperty("documents")[0].GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task Package_limits_reject_empty_non_pdf_oversized_and_too_many_documents()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter", CancellationToken.None);
        var valid = ReadFixture("valid-text.pdf");

        using var empty = await PostPackageAsync(client, presenter, Case, Array.Empty<(string, byte[])>());
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var nonPdf = await PostPackageAsync(client, presenter, Case, new[] { ("notes.txt", Encoding.UTF8.GetBytes("plain text")) });
        Assert.Equal(HttpStatusCode.BadRequest, nonPdf.StatusCode);

        using var oversizedDocument = await PostPackageAsync(client, presenter, Case, new[] { ("huge.pdf", FakePdf(10 * MiB + 1)) });
        Assert.Equal(HttpStatusCode.BadRequest, oversizedDocument.StatusCode);

        var oversizedPackage = Enumerable.Range(1, 6).Select(i => ($"part-{i}.pdf", FakePdf(9 * MiB))).ToArray();
        using var oversizedPackageResponse = await PostPackageAsync(client, presenter, Case, oversizedPackage);
        Assert.Equal(HttpStatusCode.BadRequest, oversizedPackageResponse.StatusCode);

        var tooMany = Enumerable.Range(1, 13).Select(i => ($"doc-{i}.pdf", valid)).ToArray();
        using var tooManyResponse = await PostPackageAsync(client, presenter, Case, tooMany);
        Assert.Equal(HttpStatusCode.BadRequest, tooManyResponse.StatusCode);

        Assert.Empty(Basis(factory).History($"{Case}/huge.pdf"));
    }

    [Fact]
    public async Task Mixed_package_commits_accepted_document_only_and_reports_partial()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter", CancellationToken.None);
        var files = new[] { ("statement.pdf", ReadFixture("valid-text.pdf")), ("broken.pdf", ReadFixture("corrupt.pdf")) };

        using var created = await PostPackageAsync(client, presenter, Case, files);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var package = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("partial", package.GetProperty("state").GetString());
        var documents = package.GetProperty("documents");
        Assert.Equal("accepted", documents[0].GetProperty("status").GetString());
        Assert.Equal("rejected-retryable", documents[1].GetProperty("status").GetString());

        var basis = Basis(factory);
        var committed = Assert.Single(basis.History($"{Case}/statement.pdf"));
        Assert.Equal(1, committed.Version);
        Assert.Empty(basis.History($"{Case}/broken.pdf"));
    }

    [Fact]
    public async Task Package_where_no_document_succeeds_is_failed_and_commits_nothing()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter", CancellationToken.None);

        using var created = await PostPackageAsync(client, presenter, Case, new[] { ("broken.pdf", ReadFixture("corrupt.pdf")) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var package = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("failed", package.GetProperty("state").GetString());
        Assert.Empty(Basis(factory).History($"{Case}/broken.pdf"));
    }

    [Fact]
    public async Task Retry_of_failed_document_that_parses_creates_its_next_basis_version()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter", CancellationToken.None);
        var valid = ReadFixture("valid-text.pdf");

        using var partial = await PostPackageAsync(client, presenter, Case,
            new[] { ("statement.pdf", valid), ("broken.pdf", ReadFixture("corrupt.pdf")) });
        Assert.Equal("partial", (await partial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());

        // The retry resubmits the failed filename with a document that now parses.
        using var retry = await PostPackageAsync(client, presenter, Case, new[] { ("broken.pdf", valid) });
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var package = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("complete", package.GetProperty("state").GetString());
        Assert.Equal(1, package.GetProperty("documents")[0].GetProperty("version").GetInt32());

        var basis = Basis(factory);
        var history = basis.History($"{Case}/broken.pdf");
        Assert.Single(history);
        Assert.Equal(1, history[0].Version);
        Assert.Single(basis.History($"{Case}/statement.pdf"));
    }

    [Fact]
    public async Task Resubmitting_same_names_and_bytes_returns_original_receipt_without_new_version()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter", CancellationToken.None);
        var files = new[] { ("statement.pdf", ReadFixture("valid-text.pdf")) };

        using var first = await PostPackageAsync(client, presenter, Case, files);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString();

        using var second = await PostPackageAsync(client, presenter, Case, files);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstId, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString());
        Assert.Single(Basis(factory).History($"{Case}/statement.pdf"));
    }

    [Fact]
    public async Task Changed_filename_or_document_byte_creates_a_distinct_submission()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter", CancellationToken.None);
        var valid = ReadFixture("valid-text.pdf");

        using var original = await PostPackageAsync(client, presenter, Case, new[] { ("statement.pdf", valid) });
        var originalId = (await original.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString();

        using var renamed = await PostPackageAsync(client, presenter, Case, new[] { ("renamed.pdf", valid) });
        Assert.Equal(HttpStatusCode.Created, renamed.StatusCode);
        Assert.NotEqual(originalId, (await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString());

        var changed = (byte[])valid.Clone();
        changed[^1] ^= 0xFF;
        using var edited = await PostPackageAsync(client, presenter, Case, new[] { ("statement.pdf", changed) });
        Assert.Equal(HttpStatusCode.Created, edited.StatusCode);
        Assert.NotEqual(originalId, (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString());
    }

    [Fact]
    public async Task Denied_submissions_return_their_errors_and_store_nothing()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter", CancellationToken.None);
        var reviewer = await SignInAsync(client, "reviewer", CancellationToken.None);
        var files = new[] { ("statement.pdf", ReadFixture("valid-text.pdf")) };

        using var noSession = await PostPackageAsync(client, null, Case, files);
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        Assert.Contains("Not signed in.", await noSession.Content.ReadAsStringAsync());

        using var noToken = await PostPackageAsync(client, presenter, Case, files, withCsrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        Assert.Contains("Anti-forgery validation failed.", await noToken.Content.ReadAsStringAsync());

        using var wrongRole = await PostPackageAsync(client, reviewer, Case, files);
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);
        Assert.Contains("Role not permitted.", await wrongRole.Content.ReadAsStringAsync());

        using var outOfScope = await PostPackageAsync(client, presenter, "case-999", files);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);

        var basis = Basis(factory);
        Assert.Empty(basis.History($"{Case}/statement.pdf"));
        Assert.Empty(basis.History("case-999/statement.pdf"));

        // Nothing was stored by the denied requests, so the same valid package is a new submission.
        using var accepted = await PostPackageAsync(client, presenter, Case, files);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }
}
