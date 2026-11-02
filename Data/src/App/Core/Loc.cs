using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace MultiWindowSwitcher.Core;

/// <summary>
/// Русский и английский. Язык — из настроек (auto — по языку Windows).
/// В разметке: {core:T Key}; в коде: Loc.T("Key"), Loc.F("Key", аргументы).
/// Переключение языка обновляет надписи сразу, без перезапуска.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly Dictionary<string, (string Ru, string En)> Strings = new()
    {
        // шапка и панель
        ["Dedup"] = ("Закрывать дубли папок", "Close duplicate folders"),
        ["DedupTip"] = ("Одна папка — одно окно Проводника. Уже открытые дубли закрываются сразу при запуске, а открыли папку повторно — прежнее окно с ней тихо закрывается", "One folder, one File Explorer window. Existing duplicates close right at startup; open a folder again and the older window with it closes quietly"),
        ["ToastDedupOn"] = ("Повторные окна Проводника с той же папкой будут закрываться", "Duplicate File Explorer windows of the same folder will close"),
        ["ToastDedupOff"] = ("Окна Проводника больше не закрываются", "File Explorer windows are left alone"),
        ["CornersButton"] = ("Углы и мышь", "Corners & mouse"),
        ["SetGestures"] = ("Кнопки мыши", "Mouse buttons"),
        ["MouseLeft"] = ("Левая", "Left"),
        ["MouseRight"] = ("Правая", "Right"),
        ["MouseMiddle"] = ("Средняя (колесо)", "Middle (wheel)"),
        ["MouseX1"] = ("Боковая «Назад»", "Side “Back”"),
        ["MouseX2"] = ("Боковая «Вперёд»", "Side “Forward”"),
        ["MouseNone"] = ("Нажатий нет — запишите своё", "No clicks yet — record one"),
        ["MouseRemove"] = ("Убрать нажатие", "Remove this click"),
        ["MouseRecord"] = ("Записать нажатие", "Record a click"),
        ["MouseRecording"] = ("Нажмите кнопку мыши… (левый клик — отмена)", "Press a mouse button… (left click cancels)"),
        ["MouseTip"] = ("Любая кнопка, с Ctrl / Shift / Alt, одинарный или двойной клик. Срабатывает всегда и везде, и в этом окне тоже — показать / спрятать; дальше это нажатие не передаётся", "Any button, with Ctrl / Shift / Alt, single or double. Works always and everywhere, this window too — show / hide; the click goes no further"),
        ["DesktopClick"] = ("Пустое место рабочего стола", "Empty desktop space"),
        ["DesktopClickTip"] = ("Клик левой по рабочему столу мимо значков — показать / спрятать. Сам клик Windows тоже получает", "Left click on the desktop, off the icons — show / hide. Windows still gets the click"),
        ["DesktopClickOff"] = ("Выкл", "Off"),
        ["DesktopClickSingle"] = ("Клик", "Click"),
        ["DesktopClickDouble"] = ("Двойной клик", "Double-click"),
        ["ToastMouseAdded"] = ("Записано: {0}", "Recorded: {0}"),
        ["ToastMouseExists"] = ("Уже есть: {0}", "Already there: {0}"),
        ["CornersButtonTip"] = ("Как открывать окна мышью: активные углы и кнопки мыши", "Open the windows with the mouse: hot corners and mouse buttons"),
        ["AutoHide"] = ("Прятать за иконку", "Hide behind icon"),
        ["AutoHideTip"] = ("Вкл — выбрал окно или увёл мышь, и окно уезжает за иконку у края. Выкл — обычное окно.", "On — pick a window or move the mouse away and it slides behind the edge icon. Off — a regular window."),
        ["Topmost"] = ("Поверх окон", "Always on top"),
        ["TopmostTip"] = ("Окно переключателя всегда поверх остальных", "Keep the switcher above other windows"),
        ["Search"] = ("Найти окно…", "Find a window…"),
        ["FilterAll"] = ("Все", "All"),
        ["FilterOpen"] = ("Открытые", "Open"),
        ["FilterMin"] = ("Свёрнутые", "Minimized"),
        ["FilterHidden"] = ("Скрытые", "Hidden"),
        ["FilterHiddenTip"] = ("Окна, убранные из сетки глазом. Здесь их можно вернуть или закрыть", "Windows hidden with the eye. Bring them back or close them here"),
        ["EmptyHidden"] = ("Скрытых окон нет. Глаз на плитке — убрать окно из сетки", "No hidden windows. The eye on a tile hides it from the grid"),
        ["AllMonitors"] = ("Все мониторы", "All monitors"),
        ["MonitorN"] = ("Монитор {0}", "Monitor {0}"),
        ["Aspect"] = ("Пропорции", "Aspect"),
        ["BigTitles"] = ("Вид", "Look"),
        ["TitlesButtonTip"] = ("Вид плиток: крупные названия, порядок в названии, крупные значки программ", "Tile look: big titles, title order, big app icons"),
        ["AppFirst"] = ("Перевернуть наименование", "Flip the title"),
        ["AppFirstTip"] = ("«Google Chrome - Панель byFox.dev» вместо «Панель byFox.dev - Google Chrome». Только для «Всё»", "“Google Chrome - byFox.dev Panel” instead of “byFox.dev Panel - Google Chrome”. Only with “All”"),
        ["ViewSecDock"] = ("РАСПОЛОЖЕНИЕ", "POSITION"),
        ["DockWindow"] = ("Окно", "Window"),
        ["DockTop"] = ("Лента вверху", "Top strip"),
        ["DockBottom"] = ("Лента внизу", "Bottom strip"),
        ["DockTip"] = ("На всю ширину монитора: развёрнутые окна и программы с автопозицией считают край ленты краем экрана. Управление — кнопкой «Меню», высота — потянуть за край ленты", "Full monitor width: maximized and auto-positioned apps treat the strip’s edge as the screen edge. Controls are under the Menu button; drag the strip’s edge to set its height"),
        ["HoverPreview"] = ("Большое превью при наведении", "Big preview on hover"),
        ["HoverPreviewTip"] = ("Только в ленте: мышь на плитке — окно крупно рядом с лентой, убрали — исчезло", "Strip only: hover a tile to see the window big next to the strip; move away and it’s gone"),
        ["HoverPreviewSize"] = ("Размер, от рабочей области монитора", "Size, share of the monitor’s work area"),
        ["DockToWindowTip"] = ("Вернуть в режим окна", "Back to window mode"),
        ["DockMenuTip"] = ("Меню", "Menu"),
        ["RailHideTip"] = ("Спрятать ленту и освободить место", "Hide the strip and free the space"),
        ["RailMonitorTip"] = ("Лента — на следующий монитор", "Move the strip to the next monitor"),
        ["ViewSecText"] = ("ЧТО В НАЗВАНИИ", "WHAT THE TITLE SHOWS"),
        ["ViewSecBig"] = ("КРУПНЫЕ НАЗВАНИЯ", "BIG TITLES"),
        ["ViewSecIcons"] = ("ЗНАЧКИ", "ICONS"),
        ["ViewSecWindows"] = ("КАКИЕ ОКНА", "WHICH WINDOWS"),
        ["TrayWindows"] = ("Программы из трея", "Apps in the tray"),
        ["TrayWindowsTip"] = ("Спрятанные в трей (Telegram и т. п.) тоже в сетке — со значком вместо превью. Клик — окно разворачивается", "Apps hidden to the tray (Telegram etc.) are in the grid too — an icon instead of a preview. Click to bring the window back"),
        ["InTray"] = ("в трее", "in tray"),
        ["ToastTrayOpening"] = ("«{0}» открывается — значок в трее уже нажат", "“{0}” is opening — its tray icon was already clicked"),
        ["TraySnapshots"] = ("Последний снимок вместо значка", "Last snapshot instead of the icon"),
        ["TraySnapshotsTip"] = ("Спрятанное окно Windows не рисует, живого превью нет. Пока окно на экране, раз в 5 с снимаем его — в трее виден последний снимок", "Windows doesn't draw a hidden window, so there's no live preview. While the window is on screen it's captured every 5 s — in the tray you see the last snapshot"),
        ["SnapNow"] = ("снимок только что", "snapshot just now"),
        ["SnapAgo"] = ("снимок {0} мин назад", "snapshot {0} min ago"),
        ["PartFull"] = ("Всё", "All"),
        ["PartWindow"] = ("Окно", "Window"),
        ["PartApp"] = ("Программа", "App"),
        ["PartTip"] = ("Окно — «Панель byFox.dev», программа — «Google Chrome»", "Window — “byFox.dev Panel”, app — “Google Chrome”"),
        ["BigIcons"] = ("Крупные значки программ", "Big app icons"),
        ["BigIconsTip"] = ("Значок программы крупно в левом нижнем углу превью — нужное окно находится с одного взгляда", "The app icon, big, in the bottom-left corner of each preview — spot the window at a glance"),
        ["IconSize"] = ("Размер значка", "Icon size"),
        ["BigTitlesAll"] = ("Заголовки окон крупно", "Window titles big"),
        ["BigTitlesTip"] = ("У окон без своего имени крупно виден заголовок — там же, где своё имя", "Windows without a custom name show their title big — where a name goes"),
        ["TitleSize"] = ("Размер", "Size"),
        ["TitleColor"] = ("Цвет текста", "Text color"),
        ["BannerEditTip"] = ("Изменить название", "Edit the name"),
        ["BannerRemoveTip"] = ("Убрать название", "Remove the name"),
        ["Group"] = ("Сгруппировать", "Group"),
        ["GroupTip"] = ("Окна одной программы рядом: все Chrome вместе, все Проводники вместе", "Put windows of the same app together"),
        ["RefreshTip"] = ("Обновить список и вернуть скрытые окна", "Refresh the list and bring back hidden windows"),
        ["SettingsTip"] = ("Настройки", "Settings"),
        ["Empty"] = ("Нет окон под фильтр", "No windows match"),

        // строка состояния
        ["StatusCount"] = ("{0} окон · {1} свёрнуто", "{0} windows · {1} minimized"),
        ["StatusEvents"] = (" · {0} с событием", " · {0} with events"),
        ["StatusGrid"] = ("Сетка {0}×{1}", "Grid {0}×{1}"),
        ["HintName"] = ("2× клик — имя", "double-click — name"),
        ["HintWheel"] = ("колесо ↑ — увеличить", "wheel ↑ — enlarge"),
        ["HintMiddle"] = ("средняя — закрыть", "middle — close"),
        ["HintRight"] = ("правая — меню", "right — menu"),
        ["HintDrag"] = ("перетащить — порядок", "drag — reorder"),

        // плитка
        ["Minimized"] = ("свёрнуто", "minimized"),
        ["Open"] = ("открыто", "open"),
        ["Event"] = ("событие", "event"),
        ["NextMonitorTip"] = ("На следующий монитор — и на передний план. Ещё клик — на следующий, по кругу", "To the next monitor and to the front. Click again for the next one, in a loop"),
        ["ToastMonitorMoved"] = ("«{0}» — монитор {1}", "“{0}” — monitor {1}"),
        ["ToastMonitorDenied"] = ("Windows не дала перенести «{0}»: возможно, окно запущено от администратора", "Windows wouldn’t move “{0}”: it may be running as administrator"),
        ["PinTip"] = ("Закрепить первым", "Pin first"),
        ["PeekTip"] = ("Увеличить (колесо вверх)", "Enlarge (wheel up)"),
        ["RenameTip"] = ("Дать имя (двойной клик)", "Name it (double-click)"),
        ["HideTip"] = ("Скрыть из сетки — окно будет в разделе «Скрытые»", "Hide from the grid — it goes to “Hidden”"),
        ["ShowAgainTip"] = ("Вернуть в сетку", "Back to the grid"),
        ["CloseTip"] = ("Закрыть окно (средняя кнопка)", "Close the window (middle click)"),
        ["MonitorTip"] = ("Монитор {0}", "Monitor {0}"),

        // меню окна
        ["MenuSwitch"] = ("Переключиться", "Switch to"),
        ["MenuPeek"] = ("Увеличить", "Enlarge"),
        ["MenuRename"] = ("Дать имя…", "Name…"),
        ["MenuPin"] = ("Закрепить первым", "Pin first"),
        ["MenuUnpin"] = ("Открепить", "Unpin"),
        ["MenuWide"] = ("Двойная ширина", "Double width"),
        ["MenuWideOff"] = ("Обычная ширина", "Normal width"),
        ["WideTip"] = ("Двойная ширина: окно займёт две ячейки", "Double width: the window takes two cells"),
        ["WideOffTip"] = ("Обычная ширина", "Normal width"),
        ["MenuHide"] = ("Скрыть это окно", "Hide this window"),
        ["MenuShowAgain"] = ("Вернуть в сетку", "Back to the grid"),
        ["MenuExclude"] = ("Всегда скрывать «{0}»", "Always hide “{0}”"),
        ["MenuClose"] = ("Закрыть окно", "Close window"),

        // имя окна
        ["NamePlaceholder"] = ("Своё имя окна (пусто — убрать)", "Window name (empty — remove)"),
        ["NameRule"] = ("Узнавать после перезапуска: {0}, в заголовке есть", "Recognize after restart: {0}, title contains"),

        // подсказки внизу
        ["ToastMinimized"] = ("Свёрнуто: «{0}»", "Minimized: “{0}”"),
        ["ToastMinimizeDenied"] = ("Windows не дала свернуть «{0}»: возможно, окно запущено от администратора", "Windows wouldn’t minimize “{0}”: it may be running as administrator"),
        ["ToastSwitched"] = ("Переключено на «{0}»", "Switched to “{0}”"),
        ["ToastHidden"] = ("«{0}» скрыто — оно в разделе «Скрытые»", "“{0}” hidden — see “Hidden”"),
        ["ToastShown"] = ("«{0}» снова в сетке", "“{0}” is back in the grid"),
        ["ToastExcluded"] = ("«{0}» больше не показывается · вернуть в настройках", "“{0}” is hidden for good · undo in Settings"),
        ["ToastClosed"] = ("Окно «{0}» закрыто", "Window “{0}” closed"),
        ["ToastCloseDenied"] = ("«{0}» не закрыть: оно запущено от администратора", "Can’t close “{0}”: it runs as administrator"),
        ["ToastPinned"] = ("«{0}» закреплено первым", "“{0}” pinned first"),
        ["ToastUnpinned"] = ("«{0}» откреплено", "“{0}” unpinned"),
        ["ToastGrouped"] = ("Сгруппировано: {0}", "Grouped: {0}"),
        ["ToastGroupedNone"] = ("Окна уже по программам", "Windows are already grouped"),
        ["ToastRefreshed"] = ("Список окон обновлён", "Window list refreshed"),
        ["ToastAutoHideOn"] = ("Окно прячется за иконку, когда выбрал окно или увёл мышь", "Hides behind the icon when you pick a window or move away"),
        ["ToastAutoHideOff"] = ("Обычное окно: стоит открытым, пока не закроешь", "Regular window: stays open until you close it"),

        // настройки
        ["SetLanguage"] = ("Язык", "Language"),
        ["LangAuto"] = ("Как в Windows", "Same as Windows"),
        ["SetSwitcher"] = ("Переключатель", "Switcher"),
        ["SetLive"] = ("Живые превью свёрнутых окон", "Live previews of minimized windows"),
        ["SetLiveTip"] = ("Свёрнутое окно невидимо продолжает работать, и превью идёт вживую. Выключено — превью свёрнутых замирает.", "A minimized window keeps rendering invisibly so its preview stays live. Off — minimized previews freeze."),
        ["SetAutoHideTip"] = ("Выбрал окно или увёл мышь — уезжает за край. Выключено — обычное окно", "Pick a window or move away — it slides off. Off — a regular window"),
        ["SetExclusions"] = ("Исключения", "Exclusions"),
        ["SetJunk"] = ("Не показывать служебные и пустые окна", "Hide system and empty windows"),
        ["SetJunkTip"] = ("Без заголовка, крошечные, всплывающие подсказки", "Untitled, tiny, tooltips and the like"),
        ["SetExclNone"] = ("Своих исключений нет. Правая кнопка по окну → «Всегда скрывать».", "No exclusions. Right-click a window → “Always hide”."),
        ["SetExclRow"] = ("никогда не показывать", "never shown"),
        ["SetBringBack"] = ("Снова показывать", "Show again"),
        ["SetMonitors"] = ("Мониторы", "Monitors"),
        ["SetFollowMouse"] = ("Выезжать на мониторе, где мышь", "Open on the monitor with the mouse"),
        ["SetFollowMouseTip"] = ("Иначе — всегда на основном", "Otherwise — always on the primary one"),
        ["SetStartup"] = ("Запуск", "Startup"),
        ["SetAutostart"] = ("Запускать вместе с Windows", "Start with Windows"),
        ["SetAutostartTip"] = ("Тихо, сразу спрятанным — видна только иконка у края", "Quietly, hidden — only the edge icon shows"),
        ["SetTray"] = ("Значок в трее", "Tray icon"),
        ["SetTrayTip"] = ("Меню: показать, настройки, обновления, выход", "Menu: show, settings, updates, exit"),
        ["SetCorners"] = ("Активные углы", "Hot corners"),
        ["SetCornersTip"] = ("Мышь в угол экрана — переключатель выезжает; ещё раз в угол, не выбрав окно, — уезжает. Правый нижний в Windows занят «Показать рабочий стол».", "Mouse into a screen corner opens the switcher; again, without picking — closes it. Bottom-right is Windows’ “Show desktop”."),
        ["SetOrder"] = ("Порядок окон", "Window order"),
        ["SetKeepGrouped"] = ("Новые окна ставить к своим", "Place new windows next to their app"),
        ["SetKeepGroupedTip"] = ("Открыл ещё один Chrome — он встанет рядом с остальными", "Open another Chrome — it lands next to the others"),
        ["SetFab"] = ("Плавающая кнопка", "Floating button"),
        ["SetShowFab"] = ("Показывать иконку у края", "Show the edge icon"),
        ["SetShowFabTip"] = ("Выключено — открывать из трея или активным углом", "Off — open from the tray or a hot corner"),
        ["SetSnap"] = ("Прилипать к краю экрана", "Snap to the screen edge"),
        ["SetSnapTip"] = ("Отпустил рядом с краем — встаёт вплотную", "Drop near an edge — it docks there"),
        ["SetTuck"] = ("Прятать наполовину за край", "Half-hide behind the edge"),
        ["SetTuckTip"] = ("Через 2 с уходит наполовину — иконка всегда видна", "After 2 s it slides half away — always visible"),
        ["SetHover"] = ("Открывать по наведению", "Open on hover"),
        ["SetHoverTip"] = ("Навёл на иконку — окна выехали; выбрал окно или увёл мышь — уехали", "Hover the icon — windows slide in; pick one or move away — they slide out"),
        ["SetAbout"] = ("О программе", "About"),
        ["SetVersion"] = ("Версия {0}", "Version {0}"),
        ["SetCheckUpdates"] = ("Проверить обновления", "Check for updates"),
        ["SetOpenUserData"] = ("Папка настроек", "Settings folder"),
        ["SetSite"] = ("Страница утилиты", "Utility page"),

        // обновление
        ["UpdChecking"] = ("Проверка обновлений…", "Checking for updates…"),
        ["UpdAvailable"] = ("Доступно {0}", "Update {0}"),
        ["UpdLatest"] = ("Последняя версия", "Up to date"),
        ["UpdTitle"] = ("Обновление MultiWindowSwitcher", "MultiWindowSwitcher update"),
        ["UpdHeadChecking"] = ("Проверка обновлений", "Checking for updates"),
        ["UpdInstalled"] = ("Установлена версия {0}.", "Installed version {0}."),
        ["UpdHeadAvailable"] = ("Доступна версия {0}", "Version {0} is available"),
        ["UpdSub"] = ("Установлена {0}. Программа закроется, обновится и откроется заново. Имена окон и настройки останутся.", "Installed {0}. The app will close, update and reopen. Window names and settings stay."),
        ["UpdButton"] = ("Обновить до {0}", "Update to {0}"),
        ["UpdUpdate"] = ("Обновить", "Update"),
        ["UpdClose"] = ("Закрыть", "Close"),
        ["UpdCancel"] = ("Отмена", "Cancel"),
        ["UpdStep1"] = ("Скачать архив с byfox.dev", "Download the archive from byfox.dev"),
        ["UpdStep2"] = ("Распаковать во временную папку Update", "Unpack into a temporary Update folder"),
        ["UpdStep3"] = ("Проверить содержимое архива", "Check the archive contents"),
        ["UpdStep4"] = ("Закрыть программу", "Close the app"),
        ["UpdStep5"] = ("Заменить Data и MultiWindowSwitcher.exe (UserData не трогается)", "Replace Data and MultiWindowSwitcher.exe (UserData untouched)"),
        ["UpdStep6"] = ("Поставить новую версию", "Install the new version"),
        ["UpdStep7"] = ("Удалить временную папку Update", "Remove the temporary Update folder"),
        ["UpdStep8"] = ("Запустить программу заново", "Start the app again"),
        ["UpdManifest"] = ("Проверяем манифест на byfox.dev…", "Checking the manifest on byfox.dev…"),
        ["UpdUpToDate"] = ("Установлена последняя версия {0}.", "Version {0} is the latest."),
        ["UpdFound"] = ("Доступна версия {0}.", "Version {0} is available."),
        ["UpdConnecting"] = ("Соединяемся с byfox.dev…", "Connecting to byfox.dev…"),
        ["UpdDownloading"] = ("Загрузка: {0} из {1}", "Downloading: {0} of {1}"),
        ["UpdDownloadingNoTotal"] = ("Загрузка: {0}", "Downloading: {0}"),
        ["UpdDownloaded"] = ("Загружено {0}.", "Downloaded {0}."),
        ["UpdUnpacking"] = ("Распаковка: {0} из {1} файлов", "Unpacking: {0} of {1} files"),
        ["UpdChecking2"] = ("Проверяем содержимое архива…", "Checking the archive…"),
        ["UpdPreparing"] = ("Готовим замену файлов…", "Preparing the replacement…"),
        ["UpdReady"] = ("Версия {0} скачана и готова к установке.", "Version {0} is downloaded and ready."),
        ["UpdClosing"] = ("Всё скачано и проверено. Закрываем программу — установку продолжит лаунчер.", "All downloaded and checked. Closing — the launcher finishes the install."),
        ["UpdCanceled"] = ("Обновление отменено. Временная папка удалена, программа не менялась.", "Update canceled. Temporary folder removed, nothing changed."),
        ["UpdFailed"] = ("Не получилось: {0} Программа не менялась.", "Failed: {0} Nothing changed."),
        ["UpdCheckFailed"] = ("Проверить не удалось: {0}", "Check failed: {0}"),
        ["UpdNoManifest"] = ("на byfox.dev пока не выложен манифест обновлений", "no update manifest on byfox.dev yet"),
        ["UpdNoVersion"] = ("в манифесте нет номера версии", "the manifest has no version"),
        ["UpdNoData"] = ("в архиве нет папки Data с программой — обновление отменено", "the archive has no Data folder — update canceled"),
        ["UpdNotNewer"] = ("в архиве версия {0}, она не новее установленной {1}", "the archive has {0}, not newer than installed {1}"),
        ["UpdBadPath"] = ("в архиве недопустимый путь: {0}", "invalid path in the archive: {0}"),
        ["UpdNotStaged"] = ("обновление не скачано", "the update is not downloaded"),
        ["UpdNoLauncher"] = ("не найден лаунчер {0} — он и подменяет файлы", "launcher {0} not found — it replaces the files"),

        // трей
        ["TrayShow"] = ("Показать все окна", "Show all windows"),
        ["TraySettings"] = ("Настройки", "Settings"),
        ["TrayUpdates"] = ("Проверить обновления", "Check for updates"),
        ["TrayExit"] = ("Выход", "Exit"),
        ["TrayFab"] = ("Иконка у края", "Edge icon"),
        ["ExitTip"] = ("Закрыть программу совсем. Крестик — спрятать в трей", "Quit the app. The × button hides it to the tray"),

        // плавающая кнопка
        ["FabShow"] = ("Показать все окна", "Show all windows"),
        ["FabHide"] = ("Скрыть переключатель", "Hide the switcher"),
        ["FabHideIcon"] = ("Спрятать эту иконку", "Hide this icon"),

        // прочее
        ["AlreadyRunning"] = ("Программа уже запущена, но не отвечает. Закройте её через диспетчер задач и запустите заново.", "The app is already running but not responding. End it in Task Manager and start again."),
        ["Error"] = ("Ошибка", "Error"),
    };

    private string _lang = "en";

    public static Loc I { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Язык поменялся — то, что собрано кодом, пересобирается по этому событию.</summary>
    public event Action? LanguageChanged;

    /// <summary>ru или en.</summary>
    public string Lang => _lang;

    public bool IsRu => _lang == "ru";

    public string this[string key] =>
        Strings.TryGetValue(key, out var pair) ? (_lang == "ru" ? pair.Ru : pair.En) : key;

    public static string T(string key) => I[key];

    public static string F(string key, params object[] args) => string.Format(I[key], args);

    /// <summary>auto | ru | en → фактический язык.</summary>
    public static string Resolve(string setting) => setting switch
    {
        "ru" => "ru",
        "en" => "en",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "uk" or "be" or "kk" ? "ru" : "en",
    };

    public void Apply(string setting)
    {
        var lang = Resolve(setting);

        if (lang == _lang)
        {
            return;
        }

        _lang = lang;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }

    /// <summary>Первый выбор языка при старте — без событий.</summary>
    public void Init(string setting) => _lang = Resolve(setting);
}

/// <summary>{core:T Key} — строка на текущем языке, сама меняется при переключении.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.I, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
