using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Models;

/// <summary>
/// Wire-contract guard for the POST /api/runs response. The API serializes
/// with ASP.NET Core's default camelCase policy; the run id MUST go on the
/// wire as <c>"id"</c> because andy-tasks' <c>ConductorExecutor</c> reads it
/// into <c>ContainersRunResponse.Id</c>. A casing / property-name drift here
/// yields <see cref="Guid.Empty"/> on the consumer and the misleading
/// "Executor returned no external run id." (rivoli-ai/conductor#1972).
/// </summary>
public class RunDtoSerializationTests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void RunDto_serializes_Id_as_camelCase_id()
    {
        var id = Guid.Parse("bc818dbf-a176-44ec-88a6-e168f2464239");
        var dto = new RunDto { Id = id, AgentId = "opencode", Status = RunStatus.Pending };

        var json = JsonSerializer.Serialize(dto, WireOptions);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("id", out var idProp)
            .Should().BeTrue("the run id must be emitted as camelCase \"id\" so ContainersRunResponse.Id binds");
        idProp.GetGuid().Should().Be(id);
        // The PascalCase form must not be the wire name.
        doc.RootElement.TryGetProperty("Id", out _).Should().BeFalse();
    }
}
