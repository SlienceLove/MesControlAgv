using System.Security.Cryptography;

namespace MesControlAgv.Application;

/// <summary>New workstation identities must survive the vendor's Excel export.</summary>
public static class WorkstationTaskNumber
{
    // 20 ASCII characters, with 72 random bits. Existing-task duplicate checks still apply.
    public static string Create() => "WS" + Convert.ToHexString(RandomNumberGenerator.GetBytes(9));

    public static void EnsureSafeForImport(string taskNo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskNo);
        if (taskNo.Length > 15 && taskNo.All(char.IsAsciiDigit))
            throw new ArgumentException($"任务 {taskNo}：超过 15 位的纯数字编号会在厂家 Excel 回读中丢失精度，请使用 WS 开头的字母数字编号；未执行导入。", nameof(taskNo));
    }
}
