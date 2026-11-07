// SPDX-License-Identifier: MIT
using System.Text.Json;
using SplitPlay.Core.Api;

namespace SplitPlay.Core.Tests;

public sealed class TrayWarningsTests
{
    [Fact]
    public void Parses_leniently()
    {
        var list = TrayWarnings.Parse(
            """
            [{"code":"termWrapReboot","repaired":true,"future":1},
             {"code":"termWrapUnsupported","version":"10.0.26100.7000"},
             {"code":""}, {"gpu":"x"}, 7,
             {"code":"somethingNew","repaired":"yes"}]
            """);
        TrayWarning[] expected =
        [
            new("termWrapReboot", Repaired: true),
            new("termWrapUnsupported", Version: "10.0.26100.7000"),
            new("somethingNew"),
        ];
        Assert.Equal(expected, list);
        Assert.Empty(TrayWarnings.Parse("[]"));
        Assert.ThrowsAny<JsonException>(() => TrayWarnings.Parse("{}"));
    }

    [Fact]
    public void Each_TermWrap_warning_has_one_line_and_others_none()
    {
        foreach (var code in new[] { HostWarningCodes.TermWrapReboot, HostWarningCodes.TermWrapUnsupported, HostWarningCodes.TermWrapMissing, HostWarningCodes.TermWrapRepairFailed })
        {
            Assert.False(string.IsNullOrWhiteSpace(TrayWarnings.Text(new TrayWarning(code), russian: true)));
            Assert.False(string.IsNullOrWhiteSpace(TrayWarnings.Text(new TrayWarning(code), russian: false)));
        }

        Assert.Contains("Перезагрузите ПК", TrayWarnings.Text(new TrayWarning(HostWarningCodes.TermWrapReboot, Repaired: true), russian: true), StringComparison.Ordinal);
        Assert.Contains("сбросило", TrayWarnings.Text(new TrayWarning(HostWarningCodes.TermWrapReboot, Repaired: true), russian: true), StringComparison.Ordinal);
        Assert.Null(TrayWarnings.Text(new TrayWarning(HostWarningCodes.EncoderBusy), russian: true));
        Assert.Null(TrayWarnings.Text(new TrayWarning("somethingNew"), russian: false));
    }
}
