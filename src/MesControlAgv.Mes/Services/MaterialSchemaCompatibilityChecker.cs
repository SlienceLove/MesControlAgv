using System.Data.Common;

namespace MesControlAgv.Mes.Services;

/// <summary>Validates legacy material rows before the compatibility triggers are installed.</summary>
public static class MaterialSchemaCompatibilityChecker
{
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
