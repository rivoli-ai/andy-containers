using Andy.Containers.Client;
using FluentAssertions;
using System.Net;
using Xunit;

namespace Andy.Containers.Tests;

public class ContainersClientTests
{
    [Fact]
    public void ContainersApiException_ContainsStatusCodeAndBody()
    {
        var ex = new ContainersApiException(HttpStatusCode.Forbidden, "Access denied");

        ex.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        ex.ResponseBody.Should().Be("Access denied");
        ex.Message.Should().Contain("403");
        ex.Message.Should().Contain("Access denied");
    }

    [Fact]
    public void ContainersApiException_HandlesNullBody()
    {
        var ex = new ContainersApiException(HttpStatusCode.InternalServerError, null);

        ex.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        ex.ResponseBody.Should().BeNull();
        ex.Message.Should().Contain("500");
    }

    [Fact]
    public void ContainersApiException_HandlesEmptyBody()
    {
        var ex = new ContainersApiException(HttpStatusCode.NotFound, "");

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ex.Message.Should().Contain("404");
    }

    [Fact]
    public void ContainerDto_RecordEquality()
    {
        var dto1 = new ContainersClient.ContainerDto("id1", "test", "Running", null, "user1",
            null, null, null, null, null, null, null, null, null);
        var dto2 = new ContainersClient.ContainerDto("id1", "test", "Running", null, "user1",
            null, null, null, null, null, null, null, null, null);

        dto1.Should().Be(dto2);
    }

    [Fact]
    public void ContainerDto_RecordInequality()
    {
        var dto1 = new ContainersClient.ContainerDto("id1", "test", "Running", null, "user1",
            null, null, null, null, null, null, null, null, null);
        var dto2 = new ContainersClient.ContainerDto("id2", "test", "Stopped", null, "user1",
            null, null, null, null, null, null, null, null, null);

        dto1.Should().NotBe(dto2);
    }

    [Fact]
    public void PaginatedResult_StoresItemsAndCount()
    {
        var items = new[]
        {
            new ContainersClient.ContainerDto("1", "c1", "Running", null, "u1",
                null, null, null, null, null, null, null, null, null)
        };
        var result = new ContainersClient.PaginatedResult<ContainersClient.ContainerDto>(items, 42);

        result.Items.Should().HaveCount(1);
        result.TotalCount.Should().Be(42);
    }

    [Fact]
    public void ExecResultDto_StoresAllFields()
    {
        var result = new ContainersClient.ExecResultDto(0, "hello", "warning");

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Be("hello");
        result.StdErr.Should().Be("warning");
    }

    [Fact]
    public void ContainerStatsDto_StoresAllFields()
    {
        var stats = new ContainersClient.ContainerStatsDto(45.2, 1024000, 2048000, 50.0, 500000, 1000000, 50.0);

        stats.CpuPercent.Should().Be(45.2);
        stats.MemoryUsageBytes.Should().Be(1024000);
        stats.MemoryLimitBytes.Should().Be(2048000);
        stats.MemoryPercent.Should().Be(50.0);
    }

    [Fact]
    public void ConnectionInfoDto_StoresEndpoints()
    {
        var info = new ContainersClient.ConnectionInfoDto("172.17.0.2", "ssh root@localhost -p 12345",
            "https://localhost:8080", null, null);

        info.IpAddress.Should().Be("172.17.0.2");
        info.SshEndpoint.Should().Contain("12345");
        info.IdeEndpoint.Should().Contain("8080");
        info.VncEndpoint.Should().BeNull();
    }

    [Fact]
    public void AuthenticatedHttpHandler_RequiresTokenProvider()
    {
        var act = () => new AuthenticatedHttpHandler(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ContainersClient_RequiresHttpClient()
    {
        var act = () => new ContainersClient(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
