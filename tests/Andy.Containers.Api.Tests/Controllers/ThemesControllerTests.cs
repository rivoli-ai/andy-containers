using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

// Conductor #886. The catalog endpoint is read-only — most of the
// controller's value is in the projection (palette JSON → Dict)
// and the optional kind filter. These tests pin both.
public class ThemesControllerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly ThemesController _controller;

    public ThemesControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _controller = new ThemesController(_db, NullLogger<ThemesController>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task List_ReturnsAllThemes_OrderedByDisplayName()
    {
        Seed("dracula", "Dracula", "terminal");
        Seed("nord", "Nord", "terminal");
        Seed("github-dark", "GitHub Dark", "terminal");
        await _db.SaveChangesAsync();

        var result = await _controller.List(kind: null, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value!;
        var items = (IEnumerable<ThemeDto>)payload.GetType().GetProperty("items")!.GetValue(payload)!;
        items.Should().HaveCount(3);
        items.Select(d => d.DisplayName).Should().BeEquivalentTo(
            new[] { "Dracula", "GitHub Dark", "Nord" },
            // Order is alphabetical-by-display-name to give the picker
            // a stable presentation regardless of insert order.
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task List_KindFilter_ReturnsOnlyMatchingKind()
    {
        Seed("dracula", "Dracula", "terminal");
        Seed("ide-light", "IDE Light", "ide");
        await _db.SaveChangesAsync();

        var result = await _controller.List(kind: "terminal", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var items = (IEnumerable<ThemeDto>)ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value)!;
        items.Should().HaveCount(1);
        items.Single().Id.Should().Be("dracula");
    }

    [Fact]
    public async Task List_UnknownKind_ReturnsEmptyList_Not400()
    {
        // The catalog is authoritative — an unknown filter just
        // returns no rows. Returning 400 for "kind=foo" would
        // mean the picker has to second-guess what's valid before
        // querying, which the API contract explicitly avoids.
        Seed("dracula", "Dracula", "terminal");
        await _db.SaveChangesAsync();

        var result = await _controller.List(kind: "fictional", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var items = (IEnumerable<ThemeDto>)ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value)!;
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task List_ParsesPaletteToDictionary()
    {
        // The wire shape promises a parsed palette dictionary, not
        // a raw JSON string. A DTO regression here breaks every
        // Conductor client that expects to read `theme.palette["background"]`.
        _db.Themes.Add(new Theme
        {
            Id = "dracula",
            Name = "dracula",
            DisplayName = "Dracula",
            Kind = "terminal",
            PaletteJson = "{\"background\":\"#282a36\",\"foreground\":\"#f8f8f2\"}",
            Version = 1,
        });
        await _db.SaveChangesAsync();

        var result = await _controller.List(kind: null, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var items = (IEnumerable<ThemeDto>)ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value)!;
        var dto = items.Single();
        dto.Palette["background"].Should().Be("#282a36");
        dto.Palette["foreground"].Should().Be("#f8f8f2");
    }

    [Fact]
    public async Task List_MalformedPaletteJson_ReturnsEmptyPaletteWithoutCrashing()
    {
        // Defensive: the seeder catches malformed YAML, but a row
        // with broken JSON could still make it into the catalog
        // through a manual DB edit. The endpoint must not crash —
        // it returns the row with an empty palette and the picker
        // shows a placeholder.
        _db.Themes.Add(new Theme
        {
            Id = "broken",
            Name = "broken",
            DisplayName = "Broken",
            Kind = "terminal",
            PaletteJson = "this is not json",
            Version = 1,
        });
        await _db.SaveChangesAsync();

        var result = await _controller.List(kind: null, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var items = (IEnumerable<ThemeDto>)ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value)!;
        items.Single().Palette.Should().BeEmpty();
    }

    private void Seed(string id, string display, string kind)
    {
        _db.Themes.Add(new Theme
        {
            Id = id,
            Name = id,
            DisplayName = display,
            Kind = kind,
            PaletteJson = "{\"background\":\"#000\",\"foreground\":\"#fff\"}",
            Version = 1,
        });
    }
}
