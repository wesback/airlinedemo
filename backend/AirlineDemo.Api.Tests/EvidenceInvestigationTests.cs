using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class EvidenceInvestigationTests : IDisposable
{
    private const string Passcode = "test-passcode";
    private const string Case = "case-001";

    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"case-state-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_statePath))
        {
            File.Delete(_statePath);
        }
    }

    private sealed class CountingClient : IChatClient
    {
        public int Calls { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("counting");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private WebApplicationFactory<Program> CreateFactory(IChatClient client) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = Passcode,
                    ["CaseState:Path"] = _statePath,
                }));
            builder.ConfigureTestServices(services => services.AddSingleton(client));
        });

    private static async Task<(string Cookie, string Csrf)> SignInAsync(HttpClient client, string userId)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (cookie, body.GetProperty("csrfToken").GetString()!);
    }

    private static async Task<HttpResponseMessage> PostPackageAsync(HttpClient client, (string Cookie, string Csrf) session, string fileName, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(part, "files", fileName);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/cases/{Case}/packages") { Content = form };
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Add("X-CSRF-Token", session.Csrf);
        return await client.SendAsync(request);
    }

    private CaseScope ScopeFor(ICaseStateStore store) =>
        store.OwnerOf(Case) is { } owner ? store.AuthorizeCase(Case, owner) : store.CreateCase(Case, "presenter");

    [Fact]
    public async Task PackageWithoutAcceptedDocumentsDoesNotInvokeModelOrWriteTrace()
    {
        var model = new CountingClient();
        using var factory = CreateFactory(model);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");

        // A PDF header with no body is not an accepted document.
        var notAPdf = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };
        using var response = await PostPackageAsync(client, presenter, "broken.pdf", notAPdf);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var package = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain("accepted", package.GetProperty("documents").EnumerateArray()
            .Select(d => d.GetProperty("status").GetString()));
        Assert.Equal(0, model.Calls);

        var packageId = package.GetProperty("packageId").GetString()!;
        var store = new CaseStateStore(_statePath);
        Assert.Null(store.FindTrace(ScopeFor(store), Case, packageId, "inv-" + packageId));
    }
}
