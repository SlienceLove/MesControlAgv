using MesControlAgv.Contracts.Experiments;

namespace MesControlAgv.WorkflowContract.Tests;

public sealed class ExperimentSampleVerificationContractTests
{
    [Fact]
    public void Sample_verification_contract_exposes_stable_snapshot_and_write_request_metadata()
    {
        var sampleId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var row = new ExperimentSampleTaskRow
        {
            RowId = rowId,
            SampleId = sampleId,
            SampleBarcode = " BC-001 ",
            Position = "A01",
            DisplayName = "Sample one",
            Order = 1
        };

        var sample = new ExperimentSample
        {
            SampleId = sampleId,
            BusinessSampleId = "S-001",
            BatchId = "B-001",
            Barcode = "BC-001",
            DisplayName = "Sample one",
            Status = ExperimentSampleStatus.Active
        };
        var verification = new ExperimentSampleVerification
        {
            VerificationId = Guid.NewGuid(),
            ExperimentJobId = jobId,
            Revision = 1,
            Status = ExperimentSampleVerificationStatus.ReadyForVerification,
            Rows = [row],
            SnapshotHash = "sha256",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var register = new SaveExperimentSampleRequest
        {
            RequestId = requestId,
            Actor = "operator-a",
            Reason = "register sample",
            Sample = sample
        };
        var saveRows = new SaveExperimentSampleVerificationRequest
        {
            RequestId = requestId,
            Actor = "operator-a",
            Reason = "save task rows",
            Rows = [row]
        };
        var verify = new CompleteExperimentSampleVerificationRequest
        {
            RequestId = requestId,
            Actor = "operator-a",
            Reason = "manual visual verification",
            Revision = verification.Revision,
            SnapshotHash = verification.SnapshotHash
        };

        Assert.Equal(ExperimentSampleStatus.Active, register.Sample.Status);
        Assert.Equal(ExperimentSampleVerificationStatus.ReadyForVerification, verification.Status);
        Assert.Equal(rowId, Assert.Single(saveRows.Rows).RowId);
        Assert.Equal(requestId, verify.RequestId);
        Assert.Equal("EXP-SAMPLE-NOT-FOUND", ExperimentSampleVerificationIssueCodes.SampleNotFound);
        Assert.Equal("EXP-SAMPLE-VERIFICATION-INVALIDATED", ExperimentSampleVerificationIssueCodes.VerificationInvalidated);
    }
}
