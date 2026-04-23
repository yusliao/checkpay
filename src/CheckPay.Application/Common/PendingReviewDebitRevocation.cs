using CheckPay.Application.Common.Interfaces;
using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Application.Common;

/// <summary>
/// 待核查阶段撤销银行扣款关联：支票回到「待扣款」，扣款记录回到未匹配；并清除 ACH 扣款成功标记。
/// </summary>
public static class PendingReviewDebitRevocation
{
    public static bool CanRevoke(CheckRecord check) =>
        check.Status == CheckStatus.PendingReview && check.DebitRecord != null;

    public static bool CanRevoke(CheckStatus status, DebitRecord? debit) =>
        status == CheckStatus.PendingReview && debit != null;

    public static async Task<RevokeDebitAssociationResult> TryRevokeAsync(
        IApplicationDbContext db,
        Guid checkRecordId,
        CancellationToken cancellationToken = default)
    {
        var check = await db.CheckRecords
            .Include(c => c.DebitRecord)
            .FirstOrDefaultAsync(c => c.Id == checkRecordId, cancellationToken);

        if (check == null)
            return RevokeDebitAssociationResult.Fail("支票记录不存在");

        if (!CanRevoke(check))
            return RevokeDebitAssociationResult.Fail("仅「待核查」且已关联银行扣款时可撤销扣款");

        var debit = check.DebitRecord!;
        var snapshot = new RevokeDebitAuditSnapshot(
            CheckStatus: check.Status,
            AchDebitSucceeded: check.AchDebitSucceeded,
            DebitId: debit.Id,
            DebitStatus: debit.DebitStatus);

        check.Status = CheckStatus.PendingDebit;
        check.AchDebitSucceeded = false;
        check.AchDebitSucceededAt = null;
        check.AchDebitSuccessRevokedAt = null;
        check.UpdatedAt = DateTime.UtcNow;

        debit.CheckRecordId = null;
        debit.DebitStatus = DebitStatus.Unmatched;
        debit.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        return RevokeDebitAssociationResult.Ok(snapshot);
    }
}

public sealed record RevokeDebitAuditSnapshot(
    CheckStatus CheckStatus,
    bool AchDebitSucceeded,
    Guid DebitId,
    DebitStatus DebitStatus);

public sealed class RevokeDebitAssociationResult
{
    public bool Success { get; private init; }
    public string? ErrorMessage { get; private init; }
    public RevokeDebitAuditSnapshot? Snapshot { get; private init; }

    public static RevokeDebitAssociationResult Ok(RevokeDebitAuditSnapshot snapshot) => new()
    {
        Success = true,
        Snapshot = snapshot
    };

    public static RevokeDebitAssociationResult Fail(string message) => new()
    {
        Success = false,
        ErrorMessage = message
    };
}
