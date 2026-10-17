using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Tests;

public sealed class TechnicalFinanceSourceTests
{
    private const string CoreCase = "case-001";

    private static readonly string[] FinanceKinds = ["rent", "maintenance_reserve", "security_deposit", "reserve_reimbursement"];

    private static SourceRecord<FinancePayload> FinanceRecord(string kind, IReadOnlyDictionary<string, string>? fields = null) => new()
    {
        SourceId = "finance",
        RecordId = "fin-test-001",
        RecordVersion = 1,
        AsOf = new DateOnly(2026, 10, 1),
        CaseId = CoreCase,
        Kind = kind,
        Payload = new FinancePayload(fields ?? FinanceFields(kind)),
        Synthetic = true,
    };

    private static Dictionary<string, string> FinanceFields(string kind)
    {
        var fields = new Dictionary<string, string>
        {
            ["recordedAmount"] = "100.00", ["collectedAmount"] = "75.00", ["currency"] = "USD",
            ["period"] = "2026-09", ["dueDate"] = "2026-09-30",
        };
        if (kind == "maintenance_reserve")
            fields["rateBasis"] = "per_flight_hour";
        if (kind == "maintenance_reserve") fields["rate"] = "0.10";
        if (kind == "reserve_reimbursement")
            fields["claimId"] = "claim-001";
        if (kind == "reserve_reimbursement") fields["status"] = "approved";
        return fields;
    }

    private static T Succeeded<T>(SourceReadResult<T> result) =>
        Assert.IsType<SourceReadSucceeded<T>>(result).Value;

    // Payload fields as "key=value" pairs sorted by key, so expectations do not depend on JSON order.
    private static string Flatten(IReadOnlyDictionary<string, string> fields) =>
        string.Join(";", fields.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));

    // AC-004: the core case has the exact technical and finance records; an unknown case has none.
    [Fact]
    public async Task CoreCaseHasTechnicalAndFinanceRecords()
    {
        ISourceSystem<TechnicalPayload> technical = new TechnicalSource();
        ISourceSystem<FinancePayload> finance = new FinanceSource();

        var technicalRecords = Succeeded(await technical.ListAsync(CoreCase));
        Assert.Equal(
            new (string, int, string, string, DateOnly, bool)[]
            {
                ("tech-airworthiness-001", 1, "case-001", "airworthiness_status", new DateOnly(2026, 10, 1), false),
                ("tech-component-001", 1, "case-001", "component_identity", new DateOnly(2026, 10, 1), false),
                ("tech-utilization-2026-08", 1, "case-001", "utilization", new DateOnly(2026, 8, 31), true),
                ("tech-utilization-2026-09", 1, "case-001", "utilization", new DateOnly(2026, 9, 30), true),
                ("tech-applicability-001", 1, "case-001", "applicability", new DateOnly(2026, 10, 1), false),
            },
            technicalRecords.Select(r => (r.RecordId, r.RecordVersion, r.CaseId, r.Kind, r.AsOf, r.Stale)).ToArray());
        Assert.All(technicalRecords, r => Assert.Equal("technical", r.SourceId));
        Assert.Equal(
            new[]
            {
                "directive=SYN-AD-2026-014;directiveStatus=complied;status=airworthy",
                "partNumber=SYN-ENG-7000-A;position=engine_1;serialNumber=SYN-SN-88412",
                "cycles=151;hours=198.0;periodEnd=2026-08-31;periodStart=2026-08-01",
                "cycles=164;hours=212.5;periodEnd=2026-09-30;periodStart=2026-09-01",
                "regime=EASA;requirement=SYN-AD-2026-014;responsibleParty=lessee-operator",
            },
            technicalRecords.Select(r => Flatten(r.Payload.Fields)).ToArray());

        var financeRecords = Succeeded(await finance.ListAsync(CoreCase));
        Assert.Equal(
            new (string, int, string, string, DateOnly, bool)[]
            {
                ("fin-lease-001", 1, "case-001", "lease_terms", new DateOnly(2026, 10, 1), false),
                ("fin-reserve-rate-hour-2026-08", 1, "case-001", "maintenance_reserve", new DateOnly(2026, 8, 1), true),
                ("fin-reserve-rate-hour-2026-10", 2, "case-001", "maintenance_reserve", new DateOnly(2026, 10, 1), false),
                ("fin-reserve-rate-cycle-2026-10", 1, "case-001", "maintenance_reserve", new DateOnly(2026, 10, 1), false),
            },
            financeRecords.Select(r => (r.RecordId, r.RecordVersion, r.CaseId, r.Kind, r.AsOf, r.Stale)).ToArray());
        Assert.All(financeRecords, r => Assert.Equal("finance", r.SourceId));
        Assert.Equal(
            new[]
            {
                "billingPeriod=monthly;currency=USD;leaseId=SYN-LEASE-001;reserveBasis=flight_hour_and_cycle",
                "currency=USD;effectiveFrom=2026-08-01;rate=395.00;rateBasis=per_flight_hour",
                "currency=USD;effectiveFrom=2026-10-01;rate=410.00;rateBasis=per_flight_hour",
                "currency=USD;effectiveFrom=2026-10-01;rate=275.00;rateBasis=per_cycle",
            },
            financeRecords.Select(r => Flatten(r.Payload.Fields)).ToArray());

        Assert.Empty(Succeeded(await technical.ListAsync("case-999")));
        Assert.Empty(Succeeded(await finance.ListAsync("case-999")));
    }

    // AC-004 / FR-002: repeated reads return identical records, and a record requested under another case is denied.
    [Fact]
    public async Task RepeatedReadsAreStableAndCrossCaseRecordsAreDenied()
    {
        ISourceSystem<TechnicalPayload> technical = new TechnicalSource();
        ISourceSystem<FinancePayload> finance = new FinanceSource();

        var firstList = Succeeded(await technical.ListAsync(CoreCase));
        var secondList = Succeeded(await technical.ListAsync(CoreCase));
        Assert.Equal(firstList.Select(r => r.RecordId), secondList.Select(r => r.RecordId));
        Assert.Equal(firstList.Select(r => Flatten(r.Payload.Fields)), secondList.Select(r => Flatten(r.Payload.Fields)));

        var component = Succeeded(await technical.GetAsync(CoreCase, "tech-component-001"));
        Assert.Equal("tech-component-001", component.RecordId);
        Assert.Equal(1, component.RecordVersion);
        Assert.Equal("component_identity", component.Kind);
        Assert.Equal("partNumber=SYN-ENG-7000-A;position=engine_1;serialNumber=SYN-SN-88412", Flatten(component.Payload.Fields));
        Assert.Equal(
            "partNumber=SYN-ENG-7000-A;position=engine_1;serialNumber=SYN-SN-88412",
            Flatten(Succeeded(await technical.GetAsync(CoreCase, "tech-component-001")).Payload.Fields));

        var rate = Succeeded(await finance.GetAsync(CoreCase, "fin-reserve-rate-hour-2026-10"));
        Assert.Equal(2, rate.RecordVersion);
        Assert.Equal("currency=USD;effectiveFrom=2026-10-01;rate=410.00;rateBasis=per_flight_hour", Flatten(rate.Payload.Fields));

        Assert.Equal(
            new SourceRecordNotFound<SourceRecord<TechnicalPayload>>("tech-component-001"),
            await technical.GetAsync("case-002", "tech-component-001"));
        Assert.Equal(
            new SourceRecordNotFound<SourceRecord<FinancePayload>>("fin-lease-001"),
            await finance.GetAsync("case-002", "fin-lease-001"));
        Assert.Equal(
            new SourceRecordNotFound<SourceRecord<TechnicalPayload>>("tech-missing-999"),
            await technical.GetAsync(CoreCase, "tech-missing-999"));
    }

    [Theory]
    [InlineData("rent")]
    [InlineData("maintenance_reserve")]
    [InlineData("security_deposit")]
    [InlineData("reserve_reimbursement")]
    public void Finance_records_accept_each_kind_with_its_exact_payload(string kind)
    {
        Assert.Empty(FinanceRecordValidator.Validate(FinanceRecord(kind)));
    }

    [Theory]
    [InlineData("maintenance_reserve", "rateBasis", "per_cycle")]
    [InlineData("reserve_reimbursement", "status", "claimed")]
    [InlineData("reserve_reimbursement", "status", "paid")]
    public void Finance_records_accept_each_additional_enumerated_value(string kind, string field, string value)
    {
        var fields = FinanceFields(kind);
        fields[field] = value;

        Assert.Empty(FinanceRecordValidator.Validate(FinanceRecord(kind, fields)));
    }

    [Fact]
    public void Finance_records_reject_unknown_kinds_missing_fields_bad_values_and_extra_fields()
    {
        Assert.NotEmpty(FinanceRecordValidator.Validate(FinanceRecord("unknown_kind")));
        foreach (var kind in FinanceKinds)
        {
            var missing = FinanceFields(kind);
            missing.Remove("recordedAmount");
            Assert.NotEmpty(FinanceRecordValidator.Validate(FinanceRecord(kind, missing)));
        }
        var malformed = FinanceFields("rent");
        malformed["currency"] = "usd";
        Assert.NotEmpty(FinanceRecordValidator.Validate(FinanceRecord("rent", malformed)));
        var trailingNewlineCurrency = FinanceFields("rent");
        trailingNewlineCurrency["currency"] = "USD\n";
        Assert.Equal(
            new[] { "Currency must be three uppercase letters." },
            FinanceRecordValidator.Validate(FinanceRecord("rent", trailingNewlineCurrency)));
        Assert.Equal(
            new[] { "Finance payload fields are required." },
            FinanceRecordValidator.Validate(FinanceRecord("rent") with { Payload = new FinancePayload(null!) }));
        var extra = FinanceFields("rent");
        extra["leaseId"] = "lease-001";
        Assert.NotEmpty(FinanceRecordValidator.Validate(FinanceRecord("rent", extra)));
        Assert.NotEmpty(FinanceRecordValidator.Validate(
            FinanceRecord("maintenance_reserve", FinanceFields("security_deposit"))));
    }

    [Fact]
    public void Finance_records_reject_null_field_values_without_throwing()
    {
        var fields = FinanceFields("rent");
        fields["recordedAmount"] = null!;

        Assert.Equal(
            new[] { "Finance amounts must be non-negative decimal strings." },
            FinanceRecordValidator.Validate(FinanceRecord("rent", fields)));
    }

    [Fact]
    public void FixtureBytesThatDifferFromSignedDigestAreRejected()
    {
        // AC-004: a fixture altered after signing is refused before any record is served.
        var tampered = EmbeddedBytes("AirlineDemo.Api.Fixtures.sources.technical.json").Append((byte)'\n').ToArray();

        var error = Assert.Throws<InvalidOperationException>(() =>
            FixtureSourceSystem<TechnicalPayload>.ParseFixture(
                "technical",
                "AirlineDemo.Api.Fixtures.sources.technical.json",
                TechnicalSource.CoreManifest,
                tampered,
                fields => new TechnicalPayload(fields)));

        Assert.Equal(
            "Source fixture 'AirlineDemo.Api.Fixtures.sources.technical.json' does not match its signed digest.",
            error.Message);
    }

    [Fact]
    public void FixtureRecordCountThatDiffersFromManifestIsRejected()
    {
        // AC-004: the manifest's declared record count must match the fixture's records.
        var counts = new Dictionary<string, int>(TechnicalSource.CoreManifest.RecordCounts) { ["technical"] = 4 };
        var manifest = TechnicalSource.CoreManifest with { RecordCounts = counts };

        var error = Assert.Throws<InvalidOperationException>(() =>
            FixtureSourceSystem<TechnicalPayload>.ParseFixture(
                "technical",
                "AirlineDemo.Api.Fixtures.sources.technical.json",
                manifest,
                EmbeddedBytes("AirlineDemo.Api.Fixtures.sources.technical.json"),
                fields => new TechnicalPayload(fields)));

        Assert.Equal(
            "Source fixture 'AirlineDemo.Api.Fixtures.sources.technical.json' has 5 records; manifest declares 4.",
            error.Message);
    }

    private static byte[] EmbeddedBytes(string resourceName)
    {
        using var stream = typeof(TechnicalSource).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing {resourceName}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
