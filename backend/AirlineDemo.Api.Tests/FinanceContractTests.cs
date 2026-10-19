namespace AirlineDemo.Api.Tests;

public sealed class FinanceContractTests
{
    [Fact]
    public void EveryFinancePayloadFieldIsDocumented()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".github", "workflows", "build.yml")))
        {
            directory = directory.Parent;
        }
        var contract = File.ReadAllText(Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "docs", "source-systems-contract.md"));
        foreach (var row in new[]
        {
            "| `rent` | `recordedAmount` (non-negative decimal string), `collectedAmount` (non-negative decimal string), `currency` (three uppercase letters), `period` (string, `YYYY-MM`), `dueDate` (string, `YYYY-MM-DD`) |",
            "| `maintenance_reserve` | `recordedAmount` (non-negative decimal string), `collectedAmount` (non-negative decimal string), `currency` (three uppercase letters), `period` (string, `YYYY-MM`), `dueDate` (string, `YYYY-MM-DD`), `rateBasis` (`per_flight_hour` or `per_cycle`), `rate` (non-negative decimal string) |",
            "| `security_deposit` | `recordedAmount` (non-negative decimal string), `collectedAmount` (non-negative decimal string), `currency` (three uppercase letters), `period` (string, `YYYY-MM`), `dueDate` (string, `YYYY-MM-DD`) |",
            "| `reserve_reimbursement` | `recordedAmount` (non-negative decimal string), `collectedAmount` (non-negative decimal string), `currency` (three uppercase letters), `period` (string, `YYYY-MM`), `dueDate` (string, `YYYY-MM-DD`), `claimId` (string), `status` (`claimed`, `approved` or `paid`) |",
        })
        {
            Assert.Contains(row, contract);
        }
    }
}
