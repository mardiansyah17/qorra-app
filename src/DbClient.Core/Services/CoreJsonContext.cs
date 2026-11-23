using System.Text.Json.Serialization;
using DbClient.Core.Models;

namespace DbClient.Core.Services;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<ConnectionProfile>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(WorkspaceState))]
internal partial class CoreJsonContext : JsonSerializerContext;
