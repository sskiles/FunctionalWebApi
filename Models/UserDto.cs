namespace FunctionalWebApi.Models;

using Dapper;

/// <summary>
/// Represents a user in the system.
/// </summary>
[DapperAot]
public partial record UserDto(int Id, string Name, string Email, string? PasswordHash = null);
