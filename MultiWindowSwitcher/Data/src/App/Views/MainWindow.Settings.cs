using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MultiWindowSwitcher.Core;
using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Services;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MultiWindowSwitcher.Views;

/// <summary>
/// Настройки собираются кодом при каждом открытии: список исключений всегда свежий,
/// а смена языка ничего не требует, кроме повторной сборки.
/// </summary>
public partial class MainWindow
{
    private void BuildSettings()
    {
        var s = Settings.Current;
        SettingsHost.Children.Clear();
        SettingsHost.ColumnDefinitions.Clear();

        // всё видно сразу: карточки в колонках, сколько влезает по ширине окна
        var width = Math.Clamp(ActualWidth - 24, 340, 1040);
        var columnCount = width >= 900 ? 3 : width >= 620 ? 2 : 1;
        ((Border)SettingsPopup.Child).Width = width;
        SettingsPopup.HorizontalOffset = SettingsButton.ActualWidth - width - 2;

        var columns = new List<StackPanel>();

        for (var i = 0; i < columnCount; i++)
        {
            SettingsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var column = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 6, 0, i == columnCount - 1 ? 0 : 6, 0) };
            Grid.SetColumn(column, i);
            SettingsHost.Children.Add(column);
            columns.Add(column);
        }

        // карточка → колонка: 3 колонки — окно | вызов | общее; 2 — окно | вызов + общее; 1 — подряд
        StackPanel host = null!;

        void Card(int column3, int column2)
        {
            host = new StackPanel();
            columns[columnCount == 3 ? column3 : columnCount == 2 ? column2 : 0].Children.Add(new Border
            {
                Child = host,
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 12),
                CornerRadius = new CornerRadius(7),
                Background = (Brush)FindResource("CardBrush"),
                BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
            });
        }

        // ── колонка 1: как ведёт себя окно
        Card(0, 0);
        Section(Loc.T("SetSwitcher"), first: true);
        Toggle("SetLive", "SetLiveTip", () => s.LiveMinimized, v =>
        {
            s.LiveMinimized = v;

            if (v)
            {
                Tracker.ParkAllMinimized();
            }
            else
            {
                Parking.UnparkAll();
            }

            Tracker.Refresh();
        });
        Toggle("AutoHide", "SetAutoHideTip", () => s.AutoHide, v =>
        {
            s.AutoHide = v;
            AutoHideSwitch.IsChecked = v;
            _armed = false;
        });
        Toggle("Topmost", "TopmostTip", () => s.Topmost, v =>
        {
            s.Topmost = v;
            Topmost = v;
            TopmostSwitch.IsChecked = v;
        });
        Toggle("BigTitlesAll", "BigTitlesTip", () => s.BigTitles, SetBigTitles);
        Toggle("Dedup", "DedupTip", () => s.CloseDuplicateFolders, v =>
        {
            s.CloseDuplicateFolders = v;
            DedupSwitch.IsChecked = v;
            ExplorerDuplicates.CloseAll();
        });

        Card(0, 0);
        Section(Loc.T("SetOrder"), first: true);
        Toggle("SetKeepGrouped", "SetKeepGroupedTip", () => s.KeepGrouped, v => s.KeepGrouped = v);
        Section(Loc.T("SetMonitors"));
        Toggle("SetFollowMouse", "SetFollowMouseTip", () => s.FollowMouse, v => s.FollowMouse = v);

        // ── колонка 2: чем вызывать
        Card(1, 1);
        Section(Loc.T("SetFab"), first: true);
        Toggle("SetShowFab", "SetShowFabTip", () => s.ShowFab, v =>
        {
            App.Current.SetFabVisible(v);
            Dispatcher.BeginInvoke(new Action(BuildSettings));
        });
        Toggle("SetSnap", "SetSnapTip", () => s.Snap, v =>
        {
            s.Snap = v;
            App.Current.Fab.ApplySettings();
        });
        Toggle("SetTuck", "SetTuckTip", () => s.Tuck, v =>
        {
            s.Tuck = v;
            App.Current.Fab.ApplySettings();
        });
        Toggle("SetHover", "SetHoverTip", () => s.HoverOpen, v => s.HoverOpen = v);

        // активные углы: схема экрана с четырьмя углами
        Card(1, 1);
        Section(Loc.T("SetCorners"), first: true);
        var corners = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        corners.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        corners.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        corners.Children.Add(CornerScreen(120, 72));

        var cornersTip = Small(Loc.T("SetCornersTip"));
        Grid.SetColumn(cornersTip, 1);
        corners.Children.Add(cornersTip);
        host.Children.Add(corners);

        Section(Loc.T("SetGestures"));
        var mouse = new StackPanel();
        FillMouseBindings(mouse);
        host.Children.Add(mouse);

        // ── колонка 3: общее
        Card(2, 1);
        Section(Loc.T("SetLanguage"), first: true);
        var language = new ComboBox { Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
        language.Items.Add(new ComboBoxItem { Content = Loc.T("LangAuto"), Tag = "auto" });
        language.Items.Add(new ComboBoxItem { Content = "Русский", Tag = "ru" });
        language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        language.SelectedItem = language.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Language) ?? language.Items[0];
        language.SelectionChanged += (_, _) =>
        {
            s.Language = (string)((ComboBoxItem)language.SelectedItem).Tag;
            Settings.Touch();
            Loc.I.Apply(s.Language);
            Dispatcher.BeginInvoke(new Action(BuildSettings));
        };
        host.Children.Add(language);

        Section(Loc.T("SetStartup"));
        Toggle("SetAutostart", "SetAutostartTip", () => s.Autostart, v =>
        {
            s.Autostart = v;
            Autostart.Apply(v);
        });
        Toggle("SetTray", "SetTrayTip", () => s.Tray, v =>
        {
            s.Tray = v;
            App.Current.Tray.Apply(v);

            // без трея и без иконки до программы не добраться — иконка возвращается
            if (!v && !s.ShowFab)
            {
                App.Current.SetFabVisible(true);
                Dispatcher.BeginInvoke(new Action(BuildSettings));
            }
        });

        Card(2, 1);
        Section(Loc.T("SetExclusions"), first: true);
        Toggle("SetJunk", "SetJunkTip", () => s.HideJunk, v =>
        {
            s.HideJunk = v;
            Tracker.Refresh();
        });

        if (s.Exclusions.Count == 0)
        {
            Note(Loc.T("SetExclNone"));
        }

        foreach (var exclusion in s.Exclusions.ToList())
        {
            ListRow(exclusion.AppTitle, $"{exclusion.App} · {Loc.T("SetExclRow")}", Loc.T("SetBringBack"), () =>
            {
                s.Exclusions.Remove(exclusion);
                Settings.Touch();
                Tracker.Refresh();
                BuildSettings();
            });
        }

        Card(2, 1);
        Section(Loc.T("SetAbout"), first: true);
        Note(Loc.F("SetVersion", AppVersion.Details));
        // ровной колонкой во всю ширину карточки
        var buttons = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(LinkButton(Loc.T("SetCheckUpdates"), SymbolRegular.ArrowSync24, () =>
        {
            SettingsPopup.IsOpen = false;
            ShowUpdateWindow();
        }));
        buttons.Children.Add(LinkButton(Loc.T("SetOpenUserData"), SymbolRegular.Folder24, () => Open(AppPaths.UserDataDir)));
        buttons.Children.Add(LinkButton(Loc.T("SetSite"), SymbolRegular.Globe24, () => Open(UpdateService.PageUrl)));
        host.Children.Add(buttons);

        // ── помощники

        void Section(string title, bool first = false) => host.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
            Margin = new Thickness(0, first ? 0 : 14, 0, 6),
        });

        void Note(string text) => host.Children.Add(Small(text));

        void Toggle(string titleKey, string tipKey, Func<bool> get, Action<bool> set)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = Loc.T(titleKey), TextWrapping = TextWrapping.Wrap, FontSize = 13 });
            text.Children.Add(Small(Loc.T(tipKey)));

            var toggle = new ToggleSwitch
            {
                IsChecked = get(),
                Content = text,
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            toggle.Click += (_, _) =>
            {
                AppLog.Info($"Настройка {titleKey}: {(toggle.IsChecked == true ? "вкл" : "выкл")}.");
                set(toggle.IsChecked == true);
                Settings.Touch();
            };

            host.Children.Add(toggle);
        }

        void ListRow(string title, string subtitle, string removeTip, Action remove)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(Small(subtitle));
            row.Children.Add(text);

            var button = new Button
            {
                Content = "✕",
                ToolTip = removeTip,
                Style = (Style)FindResource("DangerIconButton"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Click += (_, _) => remove();
            Grid.SetColumn(button, 1);
            row.Children.Add(button);

            host.Children.Add(new Border
            {
                Child = row,
                Padding = new Thickness(8, 5, 6, 5),
                Margin = new Thickness(0, 0, 0, 4),
                CornerRadius = new CornerRadius(5),
                Background = (Brush)FindResource("RowBrush"),
                BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
            });
        }

        TextBlock Small(string text) => new()
        {
            Text = text,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
        };

        Wpf.Ui.Controls.Button LinkButton(string text, SymbolRegular icon, Action action)
        {
            var button = new Wpf.Ui.Controls.Button
            {
                Content = text,
                Icon = new SymbolIcon { Symbol = icon },
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };

            button.Click += (_, _) => action();
            return button;
        }

        static void Open(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не открылось: " + target + " — " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Нажатия мыши: список записанных (✕ — убрать) и «Записать нажатие» — следующее нажатие
    /// мыши считывается и сохраняется. Обычный левый клик — отмена.
    /// </summary>
    private void FillMouseBindings(StackPanel panel)
    {
        var s = Settings.Current;
        s.MouseBindings ??= new();
        panel.Children.Clear();

        var tertiary = (Brush)FindResource("TextFillColorTertiaryBrush");

        if (s.MouseBindings.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = Loc.T("MouseNone"), FontSize = 11.5, Foreground = tertiary, Margin = new Thickness(0, 0, 0, 4) });
        }

        foreach (var binding in s.MouseBindings.ToList())
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = binding.Describe(), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });

            var remove = new Button
            {
                Content = "✕",
                ToolTip = Loc.T("MouseRemove"),
                Style = (Style)FindResource("DangerIconButton"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Click += (_, _) =>
            {
                s.MouseBindings.Remove(binding);
                AppLog.Info("Убрано нажатие мыши: " + binding.Describe());
                Settings.Touch();
                FillMouseBindings(panel);
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);

            panel.Children.Add(new Border
            {
                Child = row,
                Padding = new Thickness(10, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 4),
                CornerRadius = new CornerRadius(5),
                Background = (Brush)FindResource("RowBrush"),
                BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
            });
        }

        var record = new Wpf.Ui.Controls.Button
        {
            Content = Loc.T("MouseRecord"),
            Icon = new SymbolIcon { Symbol = SymbolRegular.Add24 },
            Margin = new Thickness(0, 2, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };

        record.Click += (_, _) =>
        {
            record.Content = Loc.T("MouseRecording");
            record.Appearance = ControlAppearance.Caution;
            AppLog.Info("Запись нажатия мыши…");

            App.Current.Gestures.BeginCapture(result =>
            {
                if (result is null)
                {
                    AppLog.Info("Запись нажатия отменена.");
                }
                else if (s.MouseBindings.Any(b => b.SameAs(result)))
                {
                    Toast(Loc.F("ToastMouseExists", result.Describe()));
                }
                else
                {
                    s.MouseBindings.Add(result);
                    AppLog.Info("Записано нажатие мыши: " + result.Describe());
                    Settings.Touch();
                    Toast(Loc.F("ToastMouseAdded", result.Describe()));
                }

                FillMouseBindings(panel);
            });
        };

        panel.Children.Add(record);
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("MouseTip"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = tertiary,
            Margin = new Thickness(0, 6, 0, 0),
        });

        // клик по пустому рабочему столу — отдельно от записанных нажатий: его не забирают у Windows
        var desktop = new Grid { Margin = new Thickness(0, 12, 0, 0), ToolTip = Loc.T("DesktopClickTip") };
        desktop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        desktop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        desktop.Children.Add(new TextBlock { Text = Loc.T("DesktopClick"), FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });

        var mode = new ComboBox { MinWidth = 130, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (tag, key) in new[] { ("Off", "DesktopClickOff"), ("Single", "DesktopClickSingle"), ("Double", "DesktopClickDouble") })
        {
            mode.Items.Add(new ComboBoxItem { Content = Loc.T(key), Tag = tag });
        }

        mode.SelectedItem = mode.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.DesktopClick) ?? mode.Items[0];
        mode.SelectionChanged += (_, _) =>
        {
            s.DesktopClick = (string)((ComboBoxItem)mode.SelectedItem).Tag;
            AppLog.Info("Клик по пустому рабочему столу: " + s.DesktopClick);
            Settings.Touch();
        };
        Grid.SetColumn(mode, 1);
        desktop.Children.Add(mode);
        panel.Children.Add(desktop);
    }

    /// <summary>Схема экрана: клик по углу включает или выключает активный угол.</summary>
    private Grid CornerScreen(double width, double height)
    {
        var s = Settings.Current;
        var screen = new Grid { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        screen.Children.Add(new Border
        {
            BorderBrush = (Brush)FindResource("PopupStrokeBrush"),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(5),
            Background = (Brush)FindResource("AppBackgroundBrush"),
        });
        Corner(HorizontalAlignment.Left, VerticalAlignment.Top, new CornerRadius(4, 0, 8, 0), () => s.CornerTopLeft, v => s.CornerTopLeft = v);
        Corner(HorizontalAlignment.Right, VerticalAlignment.Top, new CornerRadius(0, 4, 0, 8), () => s.CornerTopRight, v => s.CornerTopRight = v);
        Corner(HorizontalAlignment.Left, VerticalAlignment.Bottom, new CornerRadius(0, 8, 0, 4), () => s.CornerBottomLeft, v => s.CornerBottomLeft = v);
        Corner(HorizontalAlignment.Right, VerticalAlignment.Bottom, new CornerRadius(8, 0, 4, 0), () => s.CornerBottomRight, v => s.CornerBottomRight = v);
        return screen;

        void Corner(HorizontalAlignment h, VerticalAlignment v, CornerRadius radius, Func<bool> get, Action<bool> set)
        {
            var size = width >= 200 ? 30 : 22;
            var button = new ToggleButton
            {
                Style = (Style)FindResource("CornerToggle"),
                Width = size,
                Height = size,
                Tag = radius,
                IsChecked = get(),
                HorizontalAlignment = h,
                VerticalAlignment = v,
            };

            button.Click += (_, _) =>
            {
                set(button.IsChecked == true);
                AppLog.Info($"Активный угол {h}/{v}: {(button.IsChecked == true ? "вкл" : "выкл")}.");
                Settings.Touch();
            };

            screen.Children.Add(button);
        }
    }
}
