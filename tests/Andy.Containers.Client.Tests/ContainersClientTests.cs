using Andy.Containers.Client;
using FluentAssertions;
using System.Net;
using Xunit;

namespace Andy.Containers.Client.Tests;

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
    public void ContainerDto_RecordEquality()
    {
        var dto1 = new ContainersClient.ContainerDto("id1", "test", "Running", null, "user1", null, null, null, null, null, null, null, null, null, null, null);
        var dto2 = new ContainersClient.ContainerDto("id1", "test", "Running", null, "user1", null, null, null, null, null, null, null, null, null, null, null);

        dto1.Should().Be(dto2);
    }

    [Fact]
    public void PaginatedResult_StoresItemsAndCount()
    {
        var items = new[]
        {
            new ContainersClient.ContainerDto("1", "c1", "Running", null, "u1", null, null, null, null, null, null, null, null, null, null, null)
        };
        var result = new ContainersClient.PaginatedResult<ContainersClient.ContainerDto>(items, 1);

        result.Items.Should().HaveCount(1);
        result.TotalCount.Should().Be(1);
    }
}
