namespace FunctionalWebApi.Prototype;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MyActionTask = Task<Domain.Result<MyActionResult, Exception>>;

public static class UserEndpoints
{
    private static readonly Func<MyActionWebData, CancellationToken, MyActionTask> MyServiceAction;

    static UserEndpoints()
    {
        MyServiceAction = Composition.ServiceAction;
    }

    public static IApplicationBuilder MapUserEndpoints(this WebApplication app)
    {
        _ = app.MapPost("/myaction", MyActionHandler)
           .Produces<MyActionResult>()
           .Produces(StatusCodes.Status401Unauthorized)
           .WithName("MyAction");

        return app;
    }

    private static async Task<IResult> MyActionHandler(
        MyActionWebData data, CancellationToken cancellationToken)

    {
        var result = await MyServiceAction(data, cancellationToken);
        return result.Match<IResult>(
            success => Results.Ok(success),
            failure => Results.Unauthorized()
        );
    }
}
