using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using FunctionalWebApi.Contracts;
using FunctionalWebApi.Models;

namespace FunctionalWebApi;

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