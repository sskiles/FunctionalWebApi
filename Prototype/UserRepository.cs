using FunctionalWebApi.Domain;

namespace FunctionalWebApi.Prototype;

using System.Data;
using Dapper;

using MyActionTask = Task<Result<MyActionResult, Exception>>;
public static class UserRepository
{
    public static async MyActionTask MyActionAsync(
        Func<IDbConnection> openConnection, MyActionRepoData parameters, CancellationToken cancellationToken)
    {
        try
        {
            var connection = openConnection();
            var command = new CommandDefinition(
                "[schema].[MyActionProc]",
                parameters,
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken);
            return await connection.QuerySingleAsync<MyActionResult>(command);
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
