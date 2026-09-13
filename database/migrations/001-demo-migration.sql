IF OBJECT_ID(N'dbo.AirlineDemoMigration', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AirlineDemoMigration
    (
        VersionNumber int NOT NULL PRIMARY KEY,
        AppliedAtUtc datetime2(7) NOT NULL
            CONSTRAINT DF_AirlineDemoMigration_AppliedAtUtc
            DEFAULT SYSUTCDATETIME()
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM dbo.AirlineDemoMigration
    WHERE VersionNumber = 1
)
BEGIN
    INSERT INTO dbo.AirlineDemoMigration (VersionNumber)
    VALUES (1);
END;
