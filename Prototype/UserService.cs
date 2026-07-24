namespace FunctionalWebApi.Prototype;

using MyActionTask = Task<Domain.Result<MyActionResult, Exception>>;
public static class UserService
{
    public static async MyActionTask MyAction(
        Func<MyActionRepoData, CancellationToken, MyActionTask> myRepoAction,
        MyActionWebData cmd, CancellationToken cancellationToken)
    {
        var repoData = new MyActionRepoData(cmd.Id, cmd.Email, DateTime.UtcNow.AddDays(cmd.BanDays));
        return await myRepoAction(repoData, cancellationToken);
    }
}
