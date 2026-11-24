using System.Data;
using Microsoft.Data.SqlClient;

namespace EnterpriseCommerce.BuildingBlocks.Messaging;

/// <summary>
/// The relay's leadership: an exclusive, session-owned SQL Server application lock held on a dedicated, unpooled
/// connection (ADR-023 section 5). The lock lives exactly as long as the session: when the process dies or the
/// connection breaks, SQL Server releases it and a standby takes over on its next attempt. The leader checks the lock on
/// every cycle, so a broken connection ends its publishing at once.
/// </summary>
/// <remarks>
/// The lock is per database, and every service has its own database, so the instances of one service compete for it.
/// A process that freezes while its session stays open keeps the lock (a known limit, PHASE_3_S4_EVENT_FLOW.md
/// section 11).
/// </remarks>
/// <param name="connectionString">Reads the service database's connection string when the lock is first needed.</param>
/// <param name="applicationName">The application name of the lock session, which identifies it in <c>sys.dm_exec_sessions</c>.</param>
public sealed class SqlOutboxLeadership(Func<string?> connectionString, string applicationName) : IOutboxLeadership
{
    /// <summary>The application lock resource (PHASE_3_S4_EVENT_FLOW.md section 5).</summary>
    public const string LockResource = "outbox-relay";

    /// <summary>The application name of the lock session.</summary>
    public string ApplicationName { get; } = applicationName;

    private SqlConnection? _connection;

    public async Task<bool> TryEnsureAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            if (await IsStillHeldAsync(_connection, cancellationToken))
            {
                return true;
            }

            await CloseAsync();
        }

        var sessionConnectionString = new SqlConnectionStringBuilder(connectionString())
        {
            // Unpooled: closing the connection ends the session and with it the lock.
            Pooling = false,
            ApplicationName = ApplicationName,
        }.ConnectionString;
        var connection = new SqlConnection(sessionConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("sp_getapplock", connection) { CommandType = CommandType.StoredProcedure };
            command.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = LockResource });
            command.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = "Exclusive" });
            command.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Session" });
            command.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = 0 });
            var result = new SqlParameter("@Result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
            command.Parameters.Add(result);
            await command.ExecuteNonQueryAsync(cancellationToken);

            // 0: granted. -1: held by another session (a standby). Below: cancelled, deadlock or error.
            if ((int)result.Value! >= 0)
            {
                _connection = connection;
                return true;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return false;
    }

    public async Task ReleaseAsync()
    {
        if (_connection is { State: ConnectionState.Open } connection)
        {
            try
            {
                await using var command = new SqlCommand("sp_releaseapplock", connection) { CommandType = CommandType.StoredProcedure };
                command.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = LockResource });
                command.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Session" });
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (SqlException)
            {
                // Closing the unpooled connection below ends the session, which releases the lock anyway.
            }
        }

        await CloseAsync();
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();

    private static async Task<bool> IsStillHeldAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            return false;
        }

        try
        {
            await using var command = new SqlCommand("SELECT APPLOCK_MODE('public', @resource, 'Session')", connection);
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = LockResource });
            return await command.ExecuteScalarAsync(cancellationToken) is string mode && mode == "Exclusive";
        }
        catch (SqlException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task CloseAsync()
    {
        if (_connection is { } connection)
        {
            _connection = null;
            await connection.DisposeAsync();
        }
    }
}
