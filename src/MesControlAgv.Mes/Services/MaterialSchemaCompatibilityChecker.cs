using System.Data.Common;

namespace MesControlAgv.Mes.Services;

/// <summary>Validates legacy material rows before the compatibility triggers are installed.</summary>
public static class MaterialSchemaCompatibilityChecker
{
    public static async Task<IReadOnlyList<string>> FindBarcodeConflictsAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        var conflicts = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT upper(Barcode), COUNT(*)
                FROM SampleMaterials
                GROUP BY upper(Barcode)
                HAVING COUNT(*) > 1
                ORDER BY upper(Barcode)
                LIMIT 10;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                conflicts.Add($"SampleMaterials barcode '{reader.GetString(0)}' has {reader.GetInt64(1)} rows");
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT upper(Barcode), COUNT(*)
                FROM MaterialLots
                WHERE Barcode IS NOT NULL
                GROUP BY upper(Barcode)
                HAVING COUNT(*) > 1
                ORDER BY upper(Barcode)
                LIMIT 10;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                conflicts.Add($"MaterialLots barcode '{reader.GetString(0)}' has {reader.GetInt64(1)} rows");
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT upper(s.Barcode), s.SampleId, l.LotId
                FROM SampleMaterials s
                JOIN MaterialLots l ON l.Barcode IS NOT NULL AND upper(l.Barcode) = upper(s.Barcode)
                ORDER BY upper(s.Barcode)
                LIMIT 10;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                conflicts.Add(
                    $"barcode '{reader.GetString(0)}' is used by SampleMaterials/{reader.GetString(1)} and MaterialLots/{reader.GetString(2)}");
        }

        return conflicts;
    }

    public static async Task EnsureNoBarcodeConflictsAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        var conflicts = await FindBarcodeConflictsAsync(connection, cancellationToken);
        if (conflicts.Count > 0)
            throw new InvalidOperationException(
                "Material schema contains case-insensitive barcode conflicts; resolve legacy data before startup: " +
                string.Join("; ", conflicts));
    }

    public static async Task<IReadOnlyList<string>> FindOrphanForeignKeysAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        var foreignKeys = new[]
        {
            ("MaterialLots", "MaterialId", "MaterialCatalog", "MaterialId"),
            ("SampleMaterials", "LocationId", "WarehouseLocations", "LocationId"),
            ("SampleMaterials", "BoundExperimentJobId", "ExperimentJobs", "JobId"),
            ("InventoryBalances", "LotId", "MaterialLots", "LotId"),
            ("InventoryBalances", "LocationId", "WarehouseLocations", "LocationId"),
            ("InventoryTransactions", "RequestId", "MaterialOperations", "RequestId"),
            ("InventoryTransactions", "LotId", "MaterialLots", "LotId"),
            ("InventoryTransactions", "SampleId", "SampleMaterials", "SampleId"),
            ("InventoryTransactions", "FromLocationId", "WarehouseLocations", "LocationId"),
            ("InventoryTransactions", "ToLocationId", "WarehouseLocations", "LocationId"),
            ("InventoryTransactions", "ExperimentJobId", "ExperimentJobs", "JobId"),
            ("BarcodeScanEvents", "RequestId", "MaterialOperations", "RequestId"),
            ("BarcodeScanEvents", "SampleId", "SampleMaterials", "SampleId"),
            ("BarcodeScanEvents", "LotId", "MaterialLots", "LotId"),
            ("ExperimentJobMaterialBindings", "RequestId", "MaterialOperations", "RequestId"),
            ("ExperimentJobMaterialBindings", "ExperimentJobId", "ExperimentJobs", "JobId"),
            ("ExperimentJobMaterialBindings", "SampleId", "SampleMaterials", "SampleId"),
            ("ExperimentJobMaterialBindings", "LotId", "MaterialLots", "LotId")
        };
        var result = new List<string>();
        foreach (var (table, column, target, targetColumn) in foreignKeys)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table} child LEFT JOIN {target} parent ON parent.{targetColumn} = child.{column} WHERE child.{column} IS NOT NULL AND parent.{targetColumn} IS NULL;";
            var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            if (count > 0) result.Add($"{table}.{column} -> {target}.{targetColumn}: {count} orphan row(s)");
        }
        return result;
    }

    public static async Task EnsureNoOrphanForeignKeysAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        var orphans = await FindOrphanForeignKeysAsync(connection, cancellationToken);
        if (orphans.Count > 0)
            throw new InvalidOperationException("Material schema contains orphan foreign-key data: " + string.Join("; ", orphans));
    }
}
