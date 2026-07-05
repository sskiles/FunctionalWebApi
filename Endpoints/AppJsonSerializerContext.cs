namespace FunctionalWebApi;

using System.Text.Json.Serialization;
using FunctionalWebApi.Contracts;
using FunctionalWebApi.Models;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Source-generated JSON metadata for native AOT. Without this
/// <see cref="System.Text.Json"/> cannot materialise JsonTypeInfo for
/// request/response types and minimal APIs surface NotSupportedException.
/// </summary>
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(AuthToken))]
[JsonSerializable(typeof(IReadOnlyList<UserDto>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(ValidationProblemDetails))]
[JsonSerializable(typeof(Contracts.LoginCmd))]
[JsonSerializable(typeof(Contracts.CreateUserCmd))]
internal sealed partial class AppJsonSerializerContext : JsonSerializerContext
{
}
