# SplitPlay

[Русский](#русский) · [English](#english)

## Русский

SplitPlay превращает один ПК с Windows в несколько независимых рабочих столов. У каждого стола свой пользователь Windows, и каждый стол транслируется в свой клиент [Moonlight](https://moonlight-stream.org). Пока вы играете за самим ПК, второй человек играет со своего ноутбука или телевизора.

Статус: тестовые сборки (preview). Код этапа 4 готов, идут проверки на реальном железе.

### Установка

1. Скачайте `SplitPlay-Setup-*.exe` из [последнего релиза](https://github.com/echojson7/SplitPlay/releases).
2. Запустите установщик от администратора и примите предупреждение о лицензии Windows.
3. Перезагрузите ПК, если установщик попросит.
4. Откройте SplitPlay из трея или меню «Пуск» и создайте стол: пользователь Windows (существующий или новый), пароль, разрешение и частота. Нажмите «Создать и запустить».
5. На втором устройстве откройте Moonlight. Стол появится в списке сам. Moonlight покажет PIN, введите его в SplitPlay.

Требования: Windows 11 22H2 или новее (лучше 24H2), x64, сеть на 5 ГГц Wi-Fi или кабель. Видеокарта: лучше NVIDIA. Intel Arc и AMD тоже работают, но с меньшим запасом, а лимиты FPS игр есть только на NVIDIA.

### Вопросы

**Это законно?** Код SplitPlay открыт. Но для нескольких одновременных сеансов установщик ставит TermWrap, а клиентские редакции Windows это не разрешают по лицензии. Вы используете это на свой страх и риск. Удаление SplitPlay возвращает настройки Windows и NVIDIA. Пользователей столов и данные оно удаляет, только если вы это отметите.

**Нужен тестовый режим Windows или отключение Secure Boot?** Нет. SplitPlay не ставит своих драйверов. Геймпады работают через подписанный ViGEmBus.

**Moonlight не видит стол.** Сеть Windows должна быть «Частная». Проверьте, что стол запущен. Если роутер блокирует mDNS, добавьте стол в Moonlight вручную по строке «Адрес для Moonlight» на странице стола (IP:порт, например 192.168.1.5:48089).

**После обновления Windows столы не запускаются.** Обновление могло заменить `termsrv.dll`. Переустановите SplitPlay поверх и перезагрузите ПК. Если не помогло, дождитесь новой версии SplitPlay.

**Геймпад не работает.** По умолчанию стол получает виртуальный DualShock 4. Xbox 360 выбирается на странице стола в «Дополнительно», пока стол остановлен.

**Антивирус ругается.** Чаще всего на TermWrap. Добавьте папку `C:\Program Files\SplitPlay` в исключения.

**Где логи?** `C:\ProgramData\SplitPlay\logs`. Если что-то сломалось, создайте [issue](https://github.com/echojson7/SplitPlay/issues) и приложите их.

**Игры с античитом работают?** Не гарантируется. Если игра не видит мышь или клавиатуру, попробуйте «Клавиатура и мышь: RDP» на странице стола в «Дополнительно».

## English

SplitPlay turns one Windows PC into several independent desktops. Each desk has its own Windows user and streams to its own [Moonlight](https://moonlight-stream.org) client. While you play at the PC, someone else plays from a laptop or TV.

Status: preview builds. Stage 4 code is complete; testing on real hardware is in progress.

### Install

1. Download `SplitPlay-Setup-*.exe` from the [latest release](https://github.com/echojson7/SplitPlay/releases).
2. Run it as administrator and accept the Windows licensing notice.
3. Restart the PC if setup asks.
4. Open SplitPlay from the tray or the Start menu and create a desk: a Windows user (existing or new), its password, resolution and frame rate. Click "Create and start".
5. Open Moonlight on the other device. The desk shows up by itself. Moonlight shows a PIN; type it into SplitPlay.

Requirements: Windows 11 22H2 or newer (24H2 recommended), x64, 5 GHz Wi-Fi or Ethernet. GPU: NVIDIA recommended. Intel Arc and AMD work with less headroom, and game FPS limits are NVIDIA-only.

### FAQ

**Is this allowed?** SplitPlay's code is open source. For concurrent sessions the installer adds TermWrap, which Windows client editions don't permit under their licence. Use it at your own risk. Uninstalling SplitPlay restores the Windows and NVIDIA settings. It removes desk users and data only if you tick that option.

**Do I need test mode or Secure Boot off?** No. SplitPlay installs no drivers of its own. Gamepads use the signed ViGEmBus driver.

**Moonlight doesn't find the desk.** The Windows network must be Private. Check that the desk is running. If your router blocks mDNS, add the desk in Moonlight by hand using "Address for Moonlight" on the desk page (IP:port, e.g. 192.168.1.5:48089).

**Desks stopped starting after a Windows update.** The update may have replaced `termsrv.dll`. Reinstall SplitPlay over itself and restart. If that doesn't help, wait for a newer SplitPlay.

**My gamepad doesn't work.** A desk gets a virtual DualShock 4 by default. You can pick Xbox 360 under "Advanced" on the desk page while the desk is stopped.

**My antivirus complains.** Usually about TermWrap. Exclude `C:\Program Files\SplitPlay`.

**Where are the logs?** `C:\ProgramData\SplitPlay\logs`. Attach them to an [issue](https://github.com/echojson7/SplitPlay/issues) when something breaks.

**Do anti-cheat games work?** Not guaranteed. If a game ignores the mouse or keyboard, try "Keyboard and mouse: RDP" under "Advanced" on the desk page.

## For developers

Architecture (in Russian): [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Research notes: [docs/research/](docs/research/). Installer build: [installer/README.md](installer/README.md).

## License

SplitPlay's own code is MIT ([LICENSE](LICENSE)). The streaming worker in `worker/` is based on Sunshine and is GPL-3.0. Third-party components are listed in [NOTICE.md](NOTICE.md).
