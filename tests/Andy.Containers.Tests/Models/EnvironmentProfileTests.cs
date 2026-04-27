// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Models;

public class EnvironmentProfileTests
{
    [Fact]
    public void NewProfile_HasFreshId_AndCapabilityDefaults()
    {
        var profile = NewProfile();

        profile.Id.Should().NotBe(Guid.Empty);
        profile.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        profile.Capabilities.Should().NotBeNull();
        profile.Capabilities.NetworkAllowlist.Should().BeEmpty();
        profile.Capabilities.SecretsScope.Should().Be(SecretsScope.RunScoped);
        profile.Capabilities.HasGui.Should().BeFalse();
        profile.Capabilities.AuditMode.Should().Be(AuditMode.Standard);
    }

    [Fact]
    public void Capabilities_AreReplaceable()
    {
        var profile = NewProfile();

        profile.Capabilities = new EnvironmentCapabilities
        {
            NetworkAllowlist = ["api.github.com", "*.npmjs.org"],
            SecretsScope = SecretsScope.WorkspaceScoped,
            HasGui = true,
            AuditMode = AuditMode.Strict
        };

        profile.Capabilities.NetworkAllowlist.Should().HaveCount(2);
        profile.Capabilities.HasGui.Should().BeTrue();
        profile.Capabilities.AuditMode.Should().Be(AuditMode.Strict);
    }

    private static EnvironmentProfile NewProfile() => new()
    {
        Name = "headless-container",
        DisplayName = "Headless container",
        Kind = EnvironmentKind.HeadlessContainer,
        BaseImageRef = "ghcr.io/rivoli-ai/andy-headless:2026.04"
    };
}
