namespace FunctionalWebApi.Infrastructure;

using System.Text.Json.Serialization;
using FunctionalWebApi.Contracts;
using FunctionalWebApi.Models;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Source-generated JSON metadata for native AOT. Without this the
/// <see cref="System.Text.Json"/> serializer cannot materialise
/// <c>JsonTypeInfo</c> for request/response types and minimal APIs will
/// surface <see cref="NotSupportedException"/>. Lives in
/// <see cref="FunctionalWebApi.Infrastructure"/> so all cross-cutting
/// framework concerns sit alongside <c>Composition</c>.
/// </summary>
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(AuthToken))]
[JsonSerializable(typeof(IReadOnlyList<UserDto>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(ValidationProblemDetails))]
[JsonSerializable(typeof(Contracts.LoginCmd))]
[JsonSerializable(typeof(Contracts.CreateUserCmd))]
[JsonSerializable(typeof(Contracts.ChangePasswordCmd))]
internal sealed partial class AppJsonSerializerContext : JsonSerializerContext
{
}
