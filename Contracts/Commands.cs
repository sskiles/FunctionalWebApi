namespace FunctionalWebApi.Contracts;

/// <summary>
/// Command used to create a new user.
/// </summary>
public record CreateUserCmd(string Name, string Email, string Password, string ConfirmPassword);

/// <summary>
/// Command used to log a user in.
/// </summary>
public record LoginCmd(string Username, string Password);

/// <summary>
/// Token returned after a successful login.
/// </summary>
public record AuthToken(string Token);

/// <summary>
/// Command used to change a user's password.
/// </summary>
public record ChangePasswordCmd(string CurrentPassword, string NewPassword, string ConfirmNewPassword);