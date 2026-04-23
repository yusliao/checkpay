using CheckPay.Application.Common.Interfaces;
using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Application.Common;

/// <summary>
/// 待核查阶段仅撤销「ACH 扣款成功」标记（银行后续拒付/冲正时由美财操作），不解除支票与扣款记录的关联。
/// </summary>
public static class AchDebitSuccessMarkRevocation
{
    public static bool CanRevokeSuccessMark(CheckRecord check) =>
        check.Status == CheckStatus.PendingReview && check.AchDebitSucceeded;

    public static async Task<AchSuccessMarkRevokeResult> TryRevokeAsync(
        IApplicationDbContext db,
        Guid checkRecordId,
        CancellationToken cancellationToken = default)
    {
        var check = await db.CheckRecords
            .FirstOrDefaultAsync(c => c.Id == checkRecordId, cancellationToken);

        if (check == null)
            return AchSuccessMarkRevokeResult.Fail("支票记录不存在");

        if (!CanRevokeSuccessMark(check))
            return AchSuccessMarkRevokeResult.Fail("仅「待核查」且当前为 ACH 扣款成功时可撤销该标记");

        var snapshot = new AchSuccessMarkRevokeSnapshot(
            AchDebitSucceeded: check.AchDebitSucceeded,
            AchDebitSucceededAt: check.AchDebitSucceededAt);

        check.AchDebitSucceeded = false;
        check.AchDebitSucceededAt = null;
        check.AchDebitSuccessRevokedAt = DateTime.UtcNow;
        check.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        return AchSuccessMarkRevokeResult.Ok(snapshot);
    }
}

public sealed record AchSuccessMarkRevokeSnapshot(bool AchDebitSucceeded, DateTime? AchDebitSucceededAt);

public sealed class AchSuccessMarkRevokeResult
{
    public bool Success { get; private init; }
    public string? ErrorMessage { get; private init; }
    public AchSuccessMarkRevokeSnapshot? Snapshot { get; private init; }

    public static AchSuccessMarkRevokeResult Ok(AchSuccessMarkRevokeSnapshot snapshot) => new()
    {
        Success = true,
        Snapshot = snapshot
    };

    public static AchSuccessMarkRevokeResult Fail(string message) => new()
    {
        Success = false,
        ErrorMessage = message
    };
}
