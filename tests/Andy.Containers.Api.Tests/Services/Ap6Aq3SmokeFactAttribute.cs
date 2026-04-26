// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// Gates the AP6 → AQ3 cross-process smoke. Three preconditions must all
// hold; any missing piece skips the test rather than failing CI:
//
//   1. ANDY_CONTAINERS_AP6_AQ3_SMOKE=true — explicit opt-in (this is a
//      real-subprocess, real-network test, not a unit test).
//   2. ANDY_CLI_DLL — absolute path to a built andy-cli.dll, pointing at
//      a Release-built copy of andy-cli that has AQ3+ merged.
//   3. OPENAI_API_KEY — AQ3 spins up an LLM provider for a one-turn run;
//      the smoke uses gpt-4o-mini because Cerebras' is currently 402'd.
//
// Skip messages name the missing env var so a developer can fix the gap
// rather than guessing why the test is silent.
public sealed class Ap6Aq3SmokeFactAttribute : FactAttribute
{
    public const string OptInEnvVar = "ANDY_CONTAINERS_AP6_AQ3_SMOKE";
    public const string CliDllEnvVar = "ANDY_CLI_DLL";
    public const string LlmKeyEnvVar = "OPENAI_API_KEY";

    public Ap6Aq3SmokeFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInEnvVar),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = $"AP6→AQ3 smoke requires {OptInEnvVar}=true (this is a real-subprocess, real-LLM test).";
            return;
        }

        var cliDll = Environment.GetEnvironmentVariable(CliDllEnvVar);
        if (string.IsNullOrEmpty(cliDll) || !File.Exists(cliDll))
        {
            Skip = $"{CliDllEnvVar} must point to a built andy-cli.dll (got: '{cliDll ?? "<unset>"}').";
            return;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(LlmKeyEnvVar)))
        {
            Skip = $"{LlmKeyEnvVar} must be set so AQ3 can complete one LLM turn.";
        }
    }
}
