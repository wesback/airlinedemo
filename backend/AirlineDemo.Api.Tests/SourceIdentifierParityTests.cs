using System.Text.Json;

namespace AirlineDemo.Api.Tests;

public sealed class SourceIdentifierParityTests
{
    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository fixture '{relativePath}'.");
    }

    [Fact]
    public void ArrCoreRecordsUseExactEvidenceDocumentIdentity()
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(
            RepositoryFile("backend/AirlineDemo.Api.Tests/Fixtures/DemoCase/expected.json")));
        using var records = JsonDocument.Parse(File.ReadAllText(
            RepositoryFile("backend/AirlineDemo.Api/Fixtures/sources/records.json")));

        var expectedById = expected.RootElement.GetProperty("documents")
            .EnumerateArray()
            .ToDictionary(
                document => document.GetProperty("id").GetString()!,
                document => document.GetProperty("id").GetString()!,
                StringComparer.Ordinal);
        var mappings = new[]
        {
            (DocumentType: "release-to-service", DocumentId: expectedById["DOC-01"]),
            (DocumentType: "shop-visit-record", DocumentId: expectedById["DOC-02"]),
            (DocumentType: "component-release", DocumentId: expectedById["DOC-03"]),
        };
        Assert.Equal(["DOC-01", "DOC-02", "DOC-03"], mappings.Select(mapping => mapping.DocumentId));

        var coreRecords = records.RootElement.GetProperty("records")
            .EnumerateArray()
            .Where(record => record.GetProperty("caseId").GetString() == "case-001"
                && record.GetProperty("kind").GetString() == "document_index")
            .ToArray();
        Assert.Equal(3, coreRecords.Length);

        foreach (var (documentType, documentId) in mappings)
        {
            var record = Assert.Single(coreRecords, candidate =>
                candidate.GetProperty("payload").GetProperty("documentType").GetString() == documentType);
            Assert.Equal("case-001", record.GetProperty("caseId").GetString());
            Assert.Equal(documentId, record.GetProperty("payload").GetProperty("documentId").GetString());
            Assert.Equal("1", record.GetProperty("payload").GetProperty("documentVersion").GetString());
        }
    }

    [Fact]
    public void ArrCoreComponentIdentityUsesExactEvidenceIdentity()
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(
            RepositoryFile("backend/AirlineDemo.Api.Tests/Fixtures/DemoCase/expected.json")));
        using var technical = JsonDocument.Parse(File.ReadAllText(
            RepositoryFile("backend/AirlineDemo.Api/Fixtures/sources/technical.json")));

        var expectedSerial = expected.RootElement.GetProperty("scenario")
            .GetProperty("engine")
            .GetProperty("storedId")
            .GetString();
        Assert.Equal("EN-SYN-7781", expectedSerial);

        var record = Assert.Single(technical.RootElement.GetProperty("records").EnumerateArray(), candidate =>
            candidate.GetProperty("caseId").GetString() == "case-001"
            && candidate.GetProperty("kind").GetString() == "component_identity");
        Assert.Equal("case-001", record.GetProperty("caseId").GetString());
        Assert.Equal(expectedSerial, record.GetProperty("payload").GetProperty("serialNumber").GetString());
    }
}
