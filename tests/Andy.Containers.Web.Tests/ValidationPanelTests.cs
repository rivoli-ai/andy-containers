using Bunit;
using Andy.Containers.Web.Shared;
using Andy.Containers.Web.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Web.Tests;

public class ValidationPanelTests : TestContext
{
    [Fact]
    public void ShowsStartTypingMessageWhenNoResult()
    {
        var cut = RenderComponent<ValidationPanel>(p =>
            p.Add(s => s.Result, null)
             .Add(s => s.IsLoading, false));

        cut.Markup.Should().Contain("Start typing to see validation results");
    }

    [Fact]
    public void ShowsLoadingSpinnerWhenValidating()
    {
        var cut = RenderComponent<ValidationPanel>(p =>
            p.Add(s => s.IsLoading, true));

        cut.Markup.Should().Contain("Validating...");
        cut.Markup.Should().Contain("spinner-sm");
    }

    [Fact]
    public void ShowsValidMessageWhenIsValid()
    {
        var result = new YamlValidationResultDto { IsValid = true };

        var cut = RenderComponent<ValidationPanel>(p =>
            p.Add(s => s.Result, result)
             .Add(s => s.IsLoading, false));

        cut.Markup.Should().Contain("Valid");
        cut.Markup.Should().Contain("bi-check-circle-fill");
    }

    [Fact]
    public void ShowsErrorCountAndListWhenErrorsExist()
    {
        var result = new YamlValidationResultDto
        {
            IsValid = false,
            Errors =
            [
                new YamlValidationErrorDto { Field = "name", Message = "Name is required", Line = 2 },
                new YamlValidationErrorDto { Field = "code", Message = "Code is required", Line = 1 }
            ]
        };

        var cut = RenderComponent<ValidationPanel>(p =>
            p.Add(s => s.Result, result)
             .Add(s => s.IsLoading, false));

        cut.Markup.Should().Contain("2 error(s)");
        cut.Markup.Should().Contain("Name is required");
        cut.Markup.Should().Contain("Code is required");
        cut.Markup.Should().Contain("line 2");
        cut.Markup.Should().Contain("line 1");
    }

    [Fact]
    public void ShowsWarningCountAndListWhenWarningsExist()
    {
        var result = new YamlValidationResultDto
        {
            IsValid = true,
            Warnings =
            [
                new YamlValidationWarningDto { Field = "description", Message = "Description is recommended", Line = 3 }
            ]
        };

        var cut = RenderComponent<ValidationPanel>(p =>
            p.Add(s => s.Result, result)
             .Add(s => s.IsLoading, false));

        cut.Markup.Should().Contain("1 warning(s)");
        cut.Markup.Should().Contain("Description is recommended");
        cut.Markup.Should().Contain("line 3");
    }
}
