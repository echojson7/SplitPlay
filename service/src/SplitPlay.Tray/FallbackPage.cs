// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;

namespace SplitPlay.Tray;

/// <summary>
/// The page the dashboard shows instead of the web UI while the service does not answer (FallbackPage.html, an
/// embedded resource). States: <c>running</c>, <c>starting</c>, <c>stopping</c>, <c>stopped</c>, <c>missing</c>,
/// <c>notResponding</c>, <c>unknown</c>.
/// </summary>
internal static class FallbackPage
{
    private static readonly Dictionary<string, string> En = new()
    {
        ["title"] = "Services",
        ["service"] = "SplitPlay service",
        ["start"] = "Start",
        ["running"] = "Running",
        ["starting"] = "Starting",
        ["stopping"] = "Stopping",
        ["stopped"] = "Stopped",
        ["missing"] = "Not installed",
        ["notResponding"] = "Not responding",
        ["unknown"] = "Unknown",
        ["errorElevation"] = "Administrator rights are needed",
        ["errorDisabled"] = "The service is disabled",
        ["errorStart"] = "Could not start the service: {0}",
    };

    private static readonly Dictionary<string, string> Ru = new()
    {
        ["title"] = "Службы",
        ["service"] = "Служба SplitPlay",
        ["start"] = "Запустить",
        ["running"] = "Работает",
        ["starting"] = "Запускается",
        ["stopping"] = "Останавливается",
        ["stopped"] = "Остановлена",
        ["missing"] = "Не установлена",
        ["notResponding"] = "Не отвечает",
        ["unknown"] = "Нет данных",
        ["errorElevation"] = "Нужны права администратора",
        ["errorDisabled"] = "Служба отключена",
        ["errorStart"] = "Не удалось запустить службу: {0}",
    };

    private static bool Russian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

    private static Dictionary<string, string> Strings => Russian ? Ru : En;

    /// <summary>A localized string of the page (errors use the same table).</summary>
    public static string Text(string key, params object[] args) =>
        args.Length == 0 ? Strings[key] : string.Format(CultureInfo.CurrentCulture, Strings[key], args);

    /// <summary>The state pushed to the page: <c>{"status":"…","busy":false,"error":null}</c>.</summary>
    public static string StateJson(string status, bool busy, string? error) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["status"] = status, ["busy"] = busy, ["error"] = error });

    /// <summary>The whole page for NavigateToString, rendered in <paramref name="status"/>.</summary>
    public static string Build(string status, bool busy, string? error)
    {
        using var stream = typeof(FallbackPage).Assembly.GetManifestResourceStream("SplitPlay.Tray.FallbackPage.html")
                           ?? throw new InvalidOperationException("FallbackPage.html is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Replace("__LANG__", Russian ? "ru" : "en", StringComparison.Ordinal)
            .Replace("__STRINGS__", JsonSerializer.Serialize(Strings), StringComparison.Ordinal)
            .Replace("__STATE__", StateJson(status, busy, error), StringComparison.Ordinal);
    }
}
