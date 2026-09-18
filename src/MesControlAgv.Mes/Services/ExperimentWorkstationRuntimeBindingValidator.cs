using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Mes.Data;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public interface IExperimentWorkstationRuntimeBindingValidator
{
    Task<bool> IsTrustedAsync(
        Guid workflowRunId,
        Guid preparationId,
        string payloadHash,
        string deviceId,
        string vendorTaskNo,
        string sampleBarcode1,
        string sampleBarcode2,
        CancellationToken cancellationToken);
}

internal sealed class ExperimentWorkstationRuntimeBindingValidator(MesDbContext database)
    : IExperimentWorkstationRuntimeBindingValidator
{
    public async Task<bool> IsTrustedAsync(
        Guid workflowRunId,
        Guid preparationId,
        string payloadHash,
        string deviceId,
        string vendorTaskNo,
        string sampleBarcode1,
        string sampleBarcode2,
        CancellationToken cancellationToken)
    {
        var preparation = await database.ExperimentWorkstationPreparations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.PreparationId == preparationId, cancellationToken);
        if (preparation is null ||
            !string.Equals(preparation.Status, ExperimentWorkstationPreparationStatus.Imported.ToString(), StringComparison.Ordinal) ||
            !string.Equals(preparation.PayloadHash, payloadHash, StringComparison.Ordinal) ||
            !string.Equals(preparation.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(preparation.VendorTaskNo, vendorTaskNo, StringComparison.Ordinal))
            return false;
        var linked = await database.ExperimentJobs.AsNoTracking().AnyAsync(
            job => job.JobId == preparation.ExperimentJobId && job.WorkflowRunId == workflowRunId,
            cancellationToken);
        if (!linked) return false;
        var payload = ExperimentSchedulingPersistence.Deserialize<ExperimentWorkstationPreparationPayload?>(
            preparation.PayloadJson,
            null);
        return payload is not null &&
            string.Equals(payload.BottleBindings.Single(binding => binding.BottleNumber == 1).SampleBarcode, sampleBarcode1, StringComparison.Ordinal) &&
            string.Equals(payload.BottleBindings.Single(binding => binding.BottleNumber == 2).SampleBarcode, sampleBarcode2, StringComparison.Ordinal);
    }
}
