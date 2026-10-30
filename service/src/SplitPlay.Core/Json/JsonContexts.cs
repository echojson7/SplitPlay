// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Core.Json;

/// <summary>Source-generated metadata for config.json and the Web API (camelCase, enums by name).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SplitPlayConfig))]
[JsonSerializable(typeof(DeskConfig))]
[JsonSerializable(typeof(ProfileConfig))]
[JsonSerializable(typeof(HealthDto))]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(DeskDto))]
[JsonSerializable(typeof(List<DeskDto>))]
[JsonSerializable(typeof(HostUserDto))]
[JsonSerializable(typeof(List<HostUserDto>))]
[JsonSerializable(typeof(HostNetworkDto))]
[JsonSerializable(typeof(HostServiceDto))]
[JsonSerializable(typeof(List<HostServiceDto>))]
[JsonSerializable(typeof(AuthSessionDto))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(HostConfig))]
[JsonSerializable(typeof(PasswordRequest))]
[JsonSerializable(typeof(PairRequest))]
[JsonSerializable(typeof(ClientDto))]
[JsonSerializable(typeof(List<ClientDto>))]
[JsonSerializable(typeof(MetricsDto))]
[JsonSerializable(typeof(LogLineDto))]
[JsonSerializable(typeof(List<LogLineDto>))]
[JsonSerializable(typeof(WsDeskEvent))]
[JsonSerializable(typeof(WsMetricsEvent))]
public sealed partial class SplitPlayJsonContext : JsonSerializerContext;

/// <summary>Source-generated metadata for the control pipe (snake_case, nulls omitted).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(ReadyMessage))]
[JsonSerializable(typeof(HeartbeatMessage))]
[JsonSerializable(typeof(LogMessage))]
[JsonSerializable(typeof(EventMessage))]
[JsonSerializable(typeof(WelcomeMessage))]
[JsonSerializable(typeof(SetProfileMessage))]
[JsonSerializable(typeof(PairMessage))]
[JsonSerializable(typeof(ListClientsMessage))]
[JsonSerializable(typeof(UnpairMessage))]
[JsonSerializable(typeof(SendSasMessage))]
[JsonSerializable(typeof(SetModeMessage))]
[JsonSerializable(typeof(ShutdownMessage))]
[JsonSerializable(typeof(ResultMessage))]
[JsonSerializable(typeof(int))]
public sealed partial class IpcJsonContext : JsonSerializerContext;
