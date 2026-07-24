namespace FunctionalWebApi.Prototype;

using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

using MyActionTask = Task<Domain.Result<MyActionResult, Exception>>;

public static class Composition
{
    private static string _connectionString = default!;

    private static Func<IDbConnection> newConnection = default!;
    private static Func<MyActionRepoData, CancellationToken, MyActionTask> myRepoAction = default!;
    private static Func<MyActionWebData, CancellationToken, MyActionTask> myServiceAction = default!;

    public static Func<MyActionWebData, CancellationToken, MyActionTask> ServiceAction { get => myServiceAction; }

    public static async Task<IApplicationBuilder> RegisterAllEndpoints(
        this WebApplication app, IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Sqlite")!;

        newConnection = () => new SqliteConnection(_connectionString);

        myRepoAction = async (data, cancellationToken) =>
                await UserRepository.MyActionAsync(newConnection, data, cancellationToken);

        myServiceAction = async (data, cancellationToken) =>
                await UserService.MyAction(myRepoAction, data, cancellationToken);
        /*
        Func<IDbConnection> newConnection = () => new SqliteConnection(_connectionString);

        Func<MyActionRepoData, CancellationToken, MyActionTask> myRepoAction =
            async (data, cancellationToken) =>
                await UserRepository.MyActionAsync(newConnection, data, cancellationToken);

        Func<MyActionWebData, CancellationToken, MyActionTask> myServiceAction =
            async (data, cancellationToken) =>
                await UserService.MyAction(myRepoAction, data, cancellationToken);
        */


        return app.MapUserEndpoints();
    }
}

public record MyActionWebData(int Id, string Email, int BanDays);

public record MyActionRepoData(int Id, string Email, DateTime BannedUntil);

public record MyActionResult(int Id, string Email, DateTime BannedUntil);
