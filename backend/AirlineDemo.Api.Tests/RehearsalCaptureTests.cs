using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DemoCaseGenerator;

namespace AirlineDemo.Api.Tests;

public sealed class RehearsalCaptureTests
{
    private static readonly string[] Names = ["repositoryRevision", "fixtureConfigurationVersion", "deploymentId", "traceReference", "decision",
        "accessibilityResult", "applicationBuild", "browser", "assistiveTechnology", "runTimestampUtc", "outcomeDurationSeconds",
        "liveSegmentDurationSeconds", "fallbackOutcome", "fallbackFixtureId", "fallbackApplicationVersion", "journeyStates"];

    private static Dictionary<string, object?> ValidEvidence() => new()
    {
        ["repositoryRevision"] = "rev", ["fixtureConfigurationVersion"] = "fixture-v1", ["deploymentId"] = "deploy", ["traceReference"] = "trace",
        ["decision"] = "simulated-approve", ["accessibilityResult"] = "recorded", ["applicationBuild"] = "build", ["browser"] = "browser",
        ["assistiveTechnology"] = "screen reader", ["runTimestampUtc"] = "2026-10-18T02:30:00Z", ["outcomeDurationSeconds"] = 20,
        ["liveSegmentDurationSeconds"] = 90, ["fallbackOutcome"] = "used", ["fallbackFixtureId"] = "fixture",
        ["fallbackApplicationVersion"] = "fallback-v1", ["journeyStates"] = new[] { "ready" },
    };

    [Fact]
    public void ValidatesExactEvidenceTemplateContract()
    {
        var fixture = ValidEvidence();
        Assert.Equal(Names, fixture.Keys);
        RehearsalEvidence.ValidateJson(JsonSerializer.Serialize(fixture));
        var schemaPath = FindRootFile("docs/evidence/aircraft-return-readiness-rehearsal-template.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        Assert.Equal(Names.Order(), schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(Names.Order(), schema.RootElement.GetProperty("required").EnumerateArray().Select(name => name.GetString()).Order());
        var schemaFields = schema.RootElement.GetProperty("properties");
        Assert.All(Names.Take(9), name => Assert.Equal("string", schemaFields.GetProperty(name).GetProperty("type").GetString()));
        foreach (var name in new[] { "fallbackFixtureId", "fallbackApplicationVersion" })
            Assert.Equal(new[] { "string", "null" }, schemaFields.GetProperty(name).GetProperty("type").EnumerateArray().Select(type => type.GetString()));
        Assert.Equal("date-time", schemaFields.GetProperty("runTimestampUtc").GetProperty("format").GetString());
        Assert.EndsWith("Z(?![\\s\\S])", schemaFields.GetProperty("runTimestampUtc").GetProperty("pattern").GetString());
        Assert.All(new[] { "outcomeDurationSeconds", "liveSegmentDurationSeconds" }, name =>
        { var property = schemaFields.GetProperty(name); Assert.Equal("number", property.GetProperty("type").GetString()); Assert.Equal(0, property.GetProperty("minimum").GetInt32()); });
        Assert.Equal(new[] { "used", "not-used" }, schemaFields.GetProperty("fallbackOutcome").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("array", schemaFields.GetProperty("journeyStates").GetProperty("type").GetString());
        Assert.Equal(1, schemaFields.GetProperty("journeyStates").GetProperty("minItems").GetInt32());

        foreach (var name in Names)
        {
            var omitted = Copy(fixture); omitted.Remove(name); Reject(omitted);
            var wrongType = Copy(fixture); wrongType[name] = name is "outcomeDurationSeconds" or "liveSegmentDurationSeconds" ? "1"
                : name == "journeyStates" ? 1 : name == "fallbackOutcome" ? true : 1; Reject(wrongType);
        }
        var extra = Copy(fixture); extra["unlisted"] = "x"; Reject(extra);
        foreach (var name in Names.Where(name => name is not ("outcomeDurationSeconds" or "liveSegmentDurationSeconds" or "journeyStates")))
        {
            var empty = Copy(fixture); empty[name] = "";
            if (name is "fallbackFixtureId" or "fallbackApplicationVersion") empty["fallbackOutcome"] = "used";
            Reject(empty);
        }
        foreach (var invalid in new[] { "2026-02-30T02:30:00Z", "2026-10-18T02:30:00+00:00", "2026-10-18T02:30:00z", "2026-10-18T02:30:00Z\n" })
        { var changed = Copy(fixture); changed["runTimestampUtc"] = invalid; Reject(changed); }
        foreach (var name in new[] { "outcomeDurationSeconds", "liveSegmentDurationSeconds" })
        {
            var negative = Copy(fixture); negative[name] = -1; Reject(negative);
            var nullNumber = Copy(fixture); nullNumber[name] = null; Reject(nullNumber);
        }
        RejectJson(JsonSerializer.Serialize(fixture).Replace("\"outcomeDurationSeconds\":20", "\"outcomeDurationSeconds\":1e999", StringComparison.Ordinal));
        var badFallback = Copy(fixture); badFallback["fallbackOutcome"] = "sometimes"; Reject(badFallback);
        foreach (var name in new[] { "fallbackFixtureId", "fallbackApplicationVersion" })
            foreach (var absent in new object?[] { null, "" })
            { var changed = Copy(fixture); changed[name] = absent; Reject(changed); }
        var unused = Copy(fixture); unused["fallbackOutcome"] = "not-used"; unused["fallbackFixtureId"] = null; unused["fallbackApplicationVersion"] = null;
        RehearsalEvidence.ValidateJson(JsonSerializer.Serialize(unused));
        foreach (var name in new[] { "fallbackFixtureId", "fallbackApplicationVersion" })
        { var changed = Copy(unused); changed[name] = "unexpected"; Reject(changed); }
        foreach (var journey in new object?[] { Array.Empty<string>(), new[] { "" }, new object[] { 3 } })
        { var changed = Copy(fixture); changed["journeyStates"] = journey; Reject(changed); }
    }

    [Fact]
    public void RecordsMeasuredBoundariesAndRejectsUnmeasuredRuns()
    {
        Assert.Throws<InvalidOperationException>(() => new RehearsalTimingCapture(new FakeClock()).Complete());
        var exact = Measure(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(90));
        Assert.False(exact.FallbackUsed); Assert.Equal(20, exact.OutcomeDurationSeconds); Assert.Equal(90, exact.LiveSegmentDurationSeconds);
        Assert.Equal("2026-10-18T00:00:00.0000000+00:00", exact.OutcomeStartedAtUtc.ToString("O")); Assert.Equal(exact.OutcomeStartedAtUtc.AddSeconds(20), exact.OutcomeEndedAtUtc);
        Assert.Equal(exact.LiveSegmentStartedAtUtc.AddSeconds(90), exact.LiveSegmentEndedAtUtc);
        var outcomeLate = Measure(TimeSpan.FromSeconds(20).Add(TimeSpan.FromTicks(1)), TimeSpan.FromSeconds(90)); Assert.True(outcomeLate.FallbackUsed);
        Assert.Equal(20 + 1d / TimeSpan.TicksPerSecond, outcomeLate.OutcomeDurationSeconds);
        var liveLate = Measure(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(90).Add(TimeSpan.FromTicks(1))); Assert.True(liveLate.FallbackUsed);
        Assert.Equal(90 + 1d / TimeSpan.TicksPerSecond, liveLate.LiveSegmentDurationSeconds);
    }

    [Fact]
    public async Task CapturesOnlyAuthorizedStoredTrace()
    {
        const string raw = """{"caseId":"case-a","runId":"run-1","investigationId":"inv-1","basisVersion":3,"modelDeploymentName":"dep","apiVersion":"v1","region":"r","contentFilterSetting":"on","decision":"approve","actorToken":"opaque","outcome":"done","steps":[{"sequence":1,"toolName":"lookup","documentId":"doc-1","pageNumber":1,"citationCheckOutcome":"verified","correctionCode":"None","policyHandover":"None"},{"sequence":2,"toolName":"policy","documentId":"None","pageNumber":0,"citationCheckOutcome":"None","correctionCode":"None","policyHandover":"deterministic-policy"}]}""";
        var reader = new FakeReader(new(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(raw)));
        var captured = await Capture(reader);
        const string sanitized = """{"caseId":"case-a","runId":"run-1","investigationId":"inv-1","basisVersion":3,"modelDeploymentName":"dep","apiVersion":"v1","region":"r","contentFilterSetting":"on","decision":"approve","outcome":"done","steps":[{"sequence":1,"toolName":"lookup","documentId":"doc-1","pageNumber":1,"citationCheckOutcome":"verified","correctionCode":"None","policyHandover":"None"},{"sequence":2,"toolName":"policy","documentId":"None","pageNumber":0,"citationCheckOutcome":"None","correctionCode":"None","policyHandover":"deterministic-policy"}]}""";
        Assert.Equal(sanitized, System.Text.Encoding.UTF8.GetString(captured!));
        Assert.Equal(1, reader.Reads); Assert.Equal(0, reader.ModelCalls);
        using var trace = JsonDocument.Parse(captured!);
        Assert.False(trace.RootElement.TryGetProperty("actorToken", out _));
        Assert.DoesNotContain("opaque", System.Text.Encoding.UTF8.GetString(captured!), StringComparison.Ordinal);
        Assert.Equal(new[] { 1, 2 }, trace.RootElement.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("sequence").GetInt32()));
        foreach (var deniedCase in new[] { "case-out-of-scope", "case-a" })
        {
            var deniedOrAbsent = new FakeReader(new(HttpStatusCode.NotFound, []));
            Assert.Null(await Capture(deniedOrAbsent, deniedCase));
            Assert.Equal(1, deniedOrAbsent.Reads); Assert.Equal(0, deniedOrAbsent.ModelCalls);
        }
        var injected = new FakeReader(new(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(raw.Replace("\"steps\":[", "\"prompt\":\"raw-sentinel\",\"steps\":[", StringComparison.Ordinal))));
        await Assert.ThrowsAsync<InvalidDataException>(() => Capture(injected));
        await Assert.ThrowsAsync<HttpRequestException>(() => Capture(new FakeReader(new(HttpStatusCode.Unauthorized, []))));
    }

    [Fact]
    public async Task ShellScriptsDispatchWithFailurePropagation()
    {
        var root = Path.Combine(Path.GetTempPath(), "rehearsal-scripts-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "bin");
        var repositoryRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(FindRootFile("backend/DemoCaseGenerator/DemoCaseGenerator.csproj"))))!;
        Directory.CreateDirectory(bin);
        try
        {
            Stub(bin, "dotnet", "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$DOTNET_LOG\"\nexit \"$DOTNET_EXIT_CODE\"\n");
            Stub(bin, "az", "#!/bin/sh\nprintf x >> \"$TOOL_LOG\"\n"); Stub(bin, "gh", "#!/bin/sh\nprintf x >> \"$TOOL_LOG\"\n");
            var cases = new[]
            {
                ("aircraft-return-readiness-timing.sh", "capture-timing", "REHEARSAL_TIMING_OUTPUT", "missing-measurement"),
                ("aircraft-return-readiness-trace-capture.sh", "capture-trace", "REHEARSAL_TRACE_OUTPUT", "endpoint-unavailable"),
                ("aircraft-return-readiness-trace-capture.sh", "capture-trace", "REHEARSAL_TRACE_OUTPUT", "stored-trace-absent"),
            };
            foreach (var item in cases)
            {
                var log = Path.Combine(root, "args.txt"); var tools = Path.Combine(root, "tools.txt");
                var output = Path.Combine(root, item.Item4 + ".json");
                var script = Path.Combine(FindRootFile("docs/evidence/scripts/" + item.Item1));
                using var process = new Process { StartInfo = new ProcessStartInfo("/bin/sh") };
                process.StartInfo.ArgumentList.Add(script);
                process.StartInfo.Environment.Clear();
                process.StartInfo.Environment["PATH"] = bin; process.StartInfo.Environment["DOTNET_LOG"] = log;
                process.StartInfo.Environment["TOOL_LOG"] = tools; process.StartInfo.Environment["DOTNET_EXIT_CODE"] = "19";
                process.StartInfo.Environment[item.Item3] = output;
                process.Start();
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(19, process.ExitCode);
                Assert.Equal(new[] { "run", "--no-build", "--project", "backend/DemoCaseGenerator", "--", item.Item2 }, File.ReadAllLines(log));
                Assert.False(File.Exists(output)); Assert.False(File.Exists(tools));
            }

            var realDotnet = FindExecutable("dotnet", Environment.GetEnvironmentVariable("PATH")!);
            Stub(bin, "dotnet", "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$DOTNET_LOG\"\nexec \"$REAL_DOTNET\" \"$@\"\n");
            var unavailableCases = new[]
            {
                ("aircraft-return-readiness-timing.sh", "capture-timing", "REHEARSAL_TIMING_OUTPUT", "missing-measurement"),
                ("aircraft-return-readiness-trace-capture.sh", "capture-trace", "REHEARSAL_TRACE_OUTPUT", "endpoint-unavailable"),
                ("aircraft-return-readiness-trace-capture.sh", "capture-trace", "REHEARSAL_TRACE_OUTPUT", "stored-trace-absent"),
            };
            foreach (var item in unavailableCases)
            {
                var log = Path.Combine(root, "actual-args.txt");
                var tools = Path.Combine(root, "actual-tools.txt");
                var output = Path.Combine(root, item.Item4 + "-actual.json");
                using var process = new Process { StartInfo = new ProcessStartInfo("/bin/sh") };
                process.StartInfo.ArgumentList.Add(Path.Combine(FindRootFile("docs/evidence/scripts/" + item.Item1)));
                process.StartInfo.WorkingDirectory = repositoryRoot;
                process.StartInfo.RedirectStandardInput = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.Environment.Clear();
                process.StartInfo.Environment["PATH"] = bin;
                process.StartInfo.Environment["REAL_DOTNET"] = realDotnet;
                process.StartInfo.Environment["DOTNET_LOG"] = log;
                process.StartInfo.Environment["TOOL_LOG"] = tools;
                process.StartInfo.Environment[item.Item3] = output;
                if (item.Item4 == "endpoint-unavailable")
                {
                    process.StartInfo.Environment["REHEARSAL_BASE_URL"] = "http://127.0.0.1:1";
                    AddTraceEnvironment(process.StartInfo.Environment);
                }
                else if (item.Item4 == "stored-trace-absent")
                {
                    using var server = new MissingTraceServer();
                    process.StartInfo.Environment["REHEARSAL_BASE_URL"] = server.BaseUrl;
                    AddTraceEnvironment(process.StartInfo.Environment);
                    process.Start();
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                    await server.AssertRequested();
                    Assert.Equal(1, process.ExitCode);
                    Assert.Equal(new[] { "run", "--no-build", "--project", "backend/DemoCaseGenerator", "--", item.Item2 }, File.ReadAllLines(log));
                    Assert.False(File.Exists(output));
                    Assert.False(File.Exists(tools));
                    continue;
                }
                process.Start();
                process.StandardInput.Close();
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(1, process.ExitCode);
                Assert.Equal(new[] { "run", "--no-build", "--project", "backend/DemoCaseGenerator", "--", item.Item2 }, File.ReadAllLines(log));
                Assert.False(File.Exists(output));
                Assert.False(File.Exists(tools));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task<byte[]?> Capture(IStoredTraceReader reader, string caseId = "case-a") =>
        RehearsalTraceCapture.CaptureAsync(reader, new Uri("https://demo.invalid"), "session", caseId, "run-1", "inv-1",
            cancellationToken: TestContext.Current.CancellationToken);
    private static RehearsalTimingResult Measure(TimeSpan outcome, TimeSpan live)
    {
        var clock = new FakeClock(); var capture = new RehearsalTimingCapture(clock);
        capture.MarkOutcomeStart(); clock.Advance(outcome); capture.MarkOutcomeEnd(); capture.MarkLiveSegmentStart();
        clock.Advance(live); capture.MarkLiveSegmentEnd(); return capture.Complete();
    }

    private static Dictionary<string, object?> Copy(Dictionary<string, object?> fixture) => new(fixture);
    private static void Reject(Dictionary<string, object?> fixture) => RejectJson(JsonSerializer.Serialize(fixture));
    private static void RejectJson(string json) => Assert.Throws<InvalidDataException>(() => RehearsalEvidence.ValidateJson(json));
    private static string FindRootFile(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, relative); if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(relative);
    }
    private static void Stub(string bin, string name, string text)
    {
        var path = Path.Combine(bin, name); File.WriteAllText(path, text);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static string FindExecutable(string name, string path) =>
        path.Split(Path.PathSeparator).Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException($"Could not locate {name} in PATH.");
    private static void AddTraceEnvironment(IDictionary<string, string?> environment)
    {
        environment["REHEARSAL_SESSION_COOKIE"] = "session";
        environment["REHEARSAL_CASE_ID"] = "case-a";
        environment["REHEARSAL_RUN_ID"] = "run-1";
        environment["REHEARSAL_INVESTIGATION_ID"] = "inv-1";
    }
    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;
        private readonly DateTimeOffset _start = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks; public override DateTimeOffset GetUtcNow() => _start.AddTicks(_ticks);
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
    private sealed class FakeReader(StoredTraceResponse response) : IStoredTraceReader
    {
        public int Reads { get; private set; }
        public int ModelCalls => 0;
        public Task<StoredTraceResponse> ReadAsync(Uri baseUri, string sessionCookie, string caseId, string runId, string investigationId, CancellationToken cancellationToken)
        { Reads++; return Task.FromResult(response); }
    }
    private sealed class MissingTraceServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly Task _request;
        public string BaseUrl { get; }

        public MissingTraceServer()
        {
            _listener.Start();
            var endpoint = (System.Net.IPEndPoint)_listener.LocalEndpoint;
            BaseUrl = $"http://127.0.0.1:{endpoint.Port}";
            _request = RespondNotFound();
        }

        private async Task RespondNotFound()
        {
            using var client = await _listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            var request = await reader.ReadLineAsync(TestContext.Current.CancellationToken);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(TestContext.Current.CancellationToken))) { }
            Assert.Equal("GET /api/cases/case-a/dashboard/traces/run-1/inv-1 HTTP/1.1", request);
            var response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, TestContext.Current.CancellationToken);
        }

        public Task AssertRequested() => _request;
        public void Dispose() => _listener.Stop();
    }
}
