// SPDX-License-Identifier: MIT
using System.Globalization;
using SplitPlay.Core.Lifecycle;

namespace SplitPlay.Tray;

/// <summary>Texts of the tray menu and its notifications, in Russian or English as the Windows display language.</summary>
internal static class TrayText
{
    private static bool Russian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

    public static string Open => Russian ? "Открыть SplitPlay" : "Open SplitPlay";

    public static string Quit => Russian ? "Выход" : "Quit";

    public static string Start => Russian ? "Запустить" : "Start";

    public static string Stop => Russian ? "Остановить" : "Stop";

    /// <summary>Menu item and notification title: a Moonlight client shows a PIN and waits.</summary>
    public static string PinWaiting => Russian ? "Moonlight ждёт PIN" : "Moonlight is waiting for a PIN";

    /// <summary>Notification text under <see cref="PinWaiting"/>.</summary>
    public static string PinPrompt(string desks) => Russian ? $"{desks} · введите PIN" : $"{desks} · enter the PIN";

    public static string ActionFailed => Russian ? "Не получилось" : "Did not work";

    /// <summary>The state word shown next to a desk name (the web UI's words).</summary>
    public static string State(DeskState? state) => state switch
    {
        null => Russian ? "Нет данных" : "Unknown",
        DeskState.Defined => Russian ? "Не запускался" : "Not started",
        DeskState.Stopped => Russian ? "Остановлен" : "Stopped",
        DeskState.Ready => Russian ? "Готов" : "Ready",
        DeskState.Streaming => Russian ? "Трансляция" : "Streaming",
        DeskState.Recovering => Russian ? "Восстановление" : "Recovering",
        DeskState.Faulted => Russian ? "Ошибка" : "Error",
        DeskState.Stopping => Russian ? "Останавливается" : "Stopping",
        _ => Russian ? "Запускается" : "Starting",
    };
}
