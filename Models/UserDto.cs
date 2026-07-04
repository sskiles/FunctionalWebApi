using Dapper;

namespace MyApi.Models;

/// <summary>
/// Represents a user in the system.
/// </summary>
[DapperAotAttribute]
public partial record UserDto(int Id, string Name, string Email, string? PasswordHash = null);