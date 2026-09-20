using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Services;

public sealed record BankOption(string Code, string Name);

public sealed record UserBankAccountDto(
    int UserBankAccountId,
    string UserId,
    string BankCode,
    string BankName,
    string AccountNumber,
    string AccountHolderName,
    bool IsDefault,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public string MaskedAccountNumber => AccountNumber.Length <= 4
        ? new string('*', AccountNumber.Length)
        : new string('*', AccountNumber.Length - 4) + AccountNumber[^4..];
}

public interface IUserBankAccountService
{
    IReadOnlyList<BankOption> Banks { get; }

    Task<UserBankAccountDto?> GetDefaultAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task SaveDefaultAsync(
        string userId,
        string bankCode,
        string accountNumber,
        string accountHolderName,
        CancellationToken cancellationToken = default);
}

public sealed class UserBankAccountService : IUserBankAccountService
{
    private static readonly IReadOnlyList<BankOption> SupportedBanks = new[]
    {
        new BankOption("VCB", "Vietcombank"),
        new BankOption("CTG", "VietinBank"),
        new BankOption("BIDV", "BIDV"),
        new BankOption("AGR", "Agribank"),
        new BankOption("TCB", "Techcombank"),
        new BankOption("MB", "MB Bank"),
        new BankOption("ACB", "ACB"),
        new BankOption("VPB", "VPBank"),
        new BankOption("TPB", "TPBank"),
        new BankOption("STB", "Sacombank"),
        new BankOption("VIB", "VIB"),
        new BankOption("SHB", "SHB"),
        new BankOption("HDB", "HDBank"),
        new BankOption("OCB", "OCB"),
        new BankOption("MSB", "MSB"),
        new BankOption("LPB", "LPBank"),
        new BankOption("SEAB", "SeABank"),
        new BankOption("NAB", "Nam A Bank"),
        new BankOption("PGB", "PGBank"),
        new BankOption("OTHER", "Ngân hàng khác")
    };

    private readonly ApplicationDbContext _dbContext;

    public UserBankAccountService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public IReadOnlyList<BankOption> Banks => SupportedBanks;

    public async Task<UserBankAccountDto?> GetDefaultAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var connection = _dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = @"
SELECT TOP (1)
    [UserBankAccountId], [UserId], [BankCode], [BankName], [AccountNumber],
    [AccountHolderName], [IsDefault], [IsActive], [CreatedAt], [UpdatedAt]
FROM [UserBankAccounts]
WHERE [UserId] = @userId AND [IsDefault] = 1 AND [IsActive] = 1
ORDER BY [UpdatedAt] DESC;";
            AddParameter(command, "@userId", userId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new UserBankAccountDto(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetDateTime(8),
                reader.GetDateTime(9));
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task SaveDefaultAsync(
        string userId,
        string bankCode,
        string accountNumber,
        string accountHolderName,
        CancellationToken cancellationToken = default)
    {
        var bank = SupportedBanks.FirstOrDefault(item =>
            string.Equals(item.Code, bankCode, StringComparison.OrdinalIgnoreCase));
        if (bank is null)
        {
            throw new InvalidOperationException("Ngân hàng không hợp lệ.");
        }

        var normalizedBankCode = bank.Code;
        var normalizedAccountNumber = new string(accountNumber.Where(char.IsDigit).ToArray());
        var normalizedHolderName = string.Join(
            ' ',
            accountHolderName.Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

        var connection = _dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var ambientTransaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction();
        await using var ownedTransaction = ambientTransaction is null
            ? await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var transaction = ambientTransaction ?? ownedTransaction!;

        try
        {
            await using (var guard = connection.CreateCommand())
            {
                guard.Transaction = transaction;
                guard.CommandText = @"
SELECT CASE WHEN EXISTS (
    SELECT 1
    FROM [Payments] AS p WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN [Bookings] AS b WITH (UPDLOCK, HOLDLOCK)
        ON b.[BookingId] = p.[BookingId]
    WHERE b.[CustomerId] = @userId
      AND p.[Type] = 'Refund'
      AND p.[Status] = 'RefundApproved'
) THEN 1 ELSE 0 END;";
                AddParameter(guard, "@userId", userId);

                var blocked = Convert.ToInt32(
                    await guard.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
                if (blocked)
                {
                    throw new InvalidOperationException(
                        "Bạn đang có khoản hoàn tiền đã được Admin duyệt và chờ nhân viên chuyển tiền. " +
                        "Không thể đổi tài khoản nhận hoàn cho đến khi lần hoàn này hoàn tất.");
                }
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
DECLARE @now datetime2 = SYSUTCDATETIME();
DECLARE @existingId int = (
    SELECT TOP (1) [UserBankAccountId]
    FROM [UserBankAccounts]
    WHERE [UserId] = @userId AND [IsDefault] = 1 AND [IsActive] = 1
    ORDER BY [UpdatedAt] DESC
);

IF @existingId IS NULL
BEGIN
    UPDATE [UserBankAccounts]
    SET [IsDefault] = 0, [UpdatedAt] = @now
    WHERE [UserId] = @userId AND [IsDefault] = 1;

    INSERT INTO [UserBankAccounts]
        ([UserId], [BankCode], [BankName], [AccountNumber], [AccountHolderName],
         [IsDefault], [IsActive], [CreatedAt], [UpdatedAt])
    VALUES
        (@userId, @bankCode, @bankName, @accountNumber, @accountHolderName,
         1, 1, @now, @now);
END
ELSE
BEGIN
    UPDATE [UserBankAccounts]
    SET [BankCode] = @bankCode,
        [BankName] = @bankName,
        [AccountNumber] = @accountNumber,
        [AccountHolderName] = @accountHolderName,
        [IsDefault] = 1,
        [IsActive] = 1,
        [UpdatedAt] = @now
    WHERE [UserBankAccountId] = @existingId;
END";

            AddParameter(command, "@userId", userId);
            AddParameter(command, "@bankCode", normalizedBankCode);
            AddParameter(command, "@bankName", bank.Name);
            AddParameter(command, "@accountNumber", normalizedAccountNumber);
            AddParameter(command, "@accountHolderName", normalizedHolderName);

            await command.ExecuteNonQueryAsync(cancellationToken);
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
