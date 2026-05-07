using System.Data;
using System.Data.Common;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Web.Common;

public static class CheckNumberGuard
{
    private const string UniqueConstraintName = "ix_check_records_check_number";

    /// <summary>缓存：ix_check_records_check_number 是否为部分索引（含 WHERE deleted_at IS NULL）。旧版全表唯一索引会纳入软删行，需与判重一致。</summary>
    private static bool? _ixCheckNumberUniqueIsPartial;

    private static readonly SemaphoreSlim IxCheckNumberPartialProbeLock = new(1, 1);

    public static string Normalize(string? checkNumber)
        => string.IsNullOrWhiteSpace(checkNumber)
            ? string.Empty
            : checkNumber.Trim().ToUpperInvariant();

    public static string NormalizeRouting(string? routingNumber)
        => string.IsNullOrWhiteSpace(routingNumber)
            ? string.Empty
            : routingNumber.Trim();

    /// <summary>重复拦截提示：标明与库唯一索引一致的 ABA 判重键（空 = 未填/空白路由，与草稿保存一致）。</summary>
    public static string FormatDuplicateUserMessage(string normalizedCheckNumber, string normalizedRouting)
    {
        var abaLabel = string.IsNullOrEmpty(normalizedRouting) ? "空" : normalizedRouting;
        return $"同银行下支票号 {normalizedCheckNumber} 已存在（判重键 ABA：{abaLabel}）";
    }

    public static async Task<bool> ExistsAsync(
        IApplicationDbContext dbContext,
        string? checkNumber,
        string? routingNumber,
        Guid? excludeCheckRecordId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(checkNumber))
            return false;

        var trimmedCheck = checkNumber.Trim();
        var normalizedRouting = NormalizeRouting(routingNumber);
        var excludedId = excludeCheckRecordId ?? Guid.Empty;

        // PostgreSQL：计数判重；若库侧为旧版「全表」唯一索引，则软删行仍占键位，需把 deleted_at IS NOT NULL 的行一并计入（详见 IsIxCheckNumberPartialOnPostgreSqlAsync）。
        if (dbContext is ApplicationDbContext app &&
            app.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            return await ExistsOnPostgreSqlAsync(
                app,
                trimmedCheck,
                normalizedRouting,
                excludedId,
                cancellationToken);
        }

        var normalizedCheckNumber = Normalize(trimmedCheck);
        if (string.IsNullOrEmpty(normalizedCheckNumber))
            return false;

        // 遵循全局软删除过滤器：仅未删除行参与「重复支票」判断（与列表可见数据一致）。
        return await dbContext.CheckRecords
            .AsNoTracking()
            .AnyAsync(
                c => c.Id != excludedId
                     && c.CheckNumber.Trim().ToUpper() == normalizedCheckNumber
                     && (c.RoutingNumber ?? string.Empty).Trim() == normalizedRouting,
                cancellationToken);
    }

    private static async Task<bool> ExistsOnPostgreSqlAsync(
        ApplicationDbContext ctx,
        string trimmedCheck,
        string normalizedRouting,
        Guid excludedId,
        CancellationToken cancellationToken)
    {
        var connection = ctx.Database.GetDbConnection();
        var shouldClose = connection.State == ConnectionState.Closed;
        if (shouldClose)
            await ctx.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var isPartialUnique = await IsIxCheckNumberPartialOnPostgreSqlAsync(connection, cancellationToken);

            async Task<int> CountConflictsAsync(bool activeOnly)
            {
                var deletedClause = activeOnly ? "AND c.deleted_at IS NULL" : string.Empty;
                await using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    $"""
                    SELECT COUNT(*)::int
                    FROM check_records c
                    WHERE upper(btrim(c.check_number)) = upper(btrim(@p_check))
                      AND coalesce(btrim(c.routing_number), '') = @p_route
                      AND (
                          @p_exclude = '00000000-0000-0000-0000-000000000000'::uuid
                          OR c.id <> @p_exclude
                      )
                      {deletedClause}
                    """;
                var pCheck = cmd.CreateParameter();
                pCheck.ParameterName = "p_check";
                pCheck.Value = trimmedCheck;
                cmd.Parameters.Add(pCheck);

                var pRoute = cmd.CreateParameter();
                pRoute.ParameterName = "p_route";
                pRoute.Value = normalizedRouting;
                cmd.Parameters.Add(pRoute);

                var pEx = cmd.CreateParameter();
                pEx.ParameterName = "p_exclude";
                pEx.Value = excludedId;
                cmd.Parameters.Add(pEx);

                var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
                return Convert.ToInt32(scalar ?? 0);
            }

            if (await CountConflictsAsync(activeOnly: true) > 0)
                return true;

            // 旧库：唯一索引未带 WHERE deleted_at IS NULL 时，软删行仍占键位，插入会失败但仅查「未删」会得到空结果。
            if (!isPartialUnique && await CountConflictsAsync(activeOnly: false) > 0)
                return true;

            return false;
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
                await ctx.Database.CloseConnectionAsync();
        }
    }

    public static bool IsUniqueViolation(DbUpdateException exception)
    {
        Exception? current = exception;
        while (current != null)
        {
            var message = current.Message;
            if (!string.IsNullOrWhiteSpace(message) &&
                message.Contains(UniqueConstraintName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    private static async Task<bool> IsIxCheckNumberPartialOnPostgreSqlAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (_ixCheckNumberUniqueIsPartial.HasValue)
            return _ixCheckNumberUniqueIsPartial.Value;

        await IxCheckNumberPartialProbeLock.WaitAsync(cancellationToken);
        try
        {
            if (_ixCheckNumberUniqueIsPartial.HasValue)
                return _ixCheckNumberUniqueIsPartial.Value;

            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_index i
                    JOIN pg_class c ON c.oid = i.indexrelid
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE c.relname = 'ix_check_records_check_number'
                      AND n.nspname = 'public'
                      AND i.indpred IS NOT NULL
                )
                """;
            var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
            var partial = Convert.ToBoolean(scalar ?? false);
            _ixCheckNumberUniqueIsPartial = partial;
            return partial;
        }
        finally
        {
            IxCheckNumberPartialProbeLock.Release();
        }
    }
}
