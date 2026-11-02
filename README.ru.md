<div align="center">

<img src="images/icon.png" width="96" alt="Логотип MultiWindowSwitcher">

# MultiWindowSwitcher

**Все окна Windows вживую.**

Все открытые и свёрнутые окна — живыми превью в одной сетке.
Навёл на иконку у края — увидел, кликнул — переключился.

[![Скачать .zip](https://img.shields.io/badge/%D0%A1%D0%BA%D0%B0%D1%87%D0%B0%D1%82%D1%8C-MultiWindowSwitcher.zip-2ea44f?style=for-the-badge)](https://byfox.dev/data/multiwindowswitcher/MultiWindowSwitcher.zip)
[![Демонстрация](https://img.shields.io/badge/%D0%94%D0%B5%D0%BC%D0%BE%D0%BD%D1%81%D1%82%D1%80%D0%B0%D1%86%D0%B8%D1%8F-%D0%B2%20%D0%B1%D1%80%D0%B0%D1%83%D0%B7%D0%B5%D1%80%D0%B5-4CA6FF?style=for-the-badge)](https://byfox.dev/multiwindowswitcher/demo/?lang=ru)
[![Сайт](https://img.shields.io/badge/%D0%A1%D0%B0%D0%B9%D1%82-byfox.dev-4ecdc4?style=for-the-badge)](https://byfox.dev/multiwindowswitcher/)

![Windows 10 / 11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?logo=windows)
![Русский · English](https://img.shields.io/badge/%D0%B8%D0%BD%D1%82%D0%B5%D1%80%D1%84%D0%B5%D0%B9%D1%81-%D0%A0%D1%83%D1%81%D1%81%D0%BA%D0%B8%D0%B9%20%C2%B7%20English-444)
![Бесплатно](https://img.shields.io/badge/%D1%86%D0%B5%D0%BD%D0%B0-%D0%B1%D0%B5%D1%81%D0%BF%D0%BB%D0%B0%D1%82%D0%BD%D0%BE-2ea44f)
[![Лицензия: GPL-3.0](https://img.shields.io/badge/лицензия-GPL--3.0-blue)](LICENSE)

[English](README.md) · **Русский**

</div>

Видно, что происходит во всех окнах сразу — даже в свёрнутых. Нужное выбрал — переключился, просто посмотрел — увёл мышь, и всё уехало.

- **Живые превью** — все окна в ровной сетке, превью идут вживую. Свёрнутые тоже: окно незаметно продолжает работать.
- **Выезжает от края** — иконка у края, активный угол или трей. Окно выезжает поверх любой программы, ничего не сдвигая, и уезжает само.
- **Свои имена** — двойной клик, и у окна крупное имя: «Сервер», «Работа». Окно узнаётся и после перезапуска.
- **Порядок** — сетка подстраивается под размер. **Сгруппировать** ставит окна одной программы рядом, закреплённые — первыми.
- **События** — окно мигает на панели задач, плитка и иконка подсвечиваются.
- **Всё мышью** — клик — переключиться, средняя кнопка — закрыть, правая — меню. Показать окна — любым своим нажатием мыши по пустому заголовку или рабочему столу.
- **Закрывать дубли папок** — одна папка, одно окно Проводника: дубли закрываются при запуске и когда папку открыли ещё раз.

## Запуск

1. Распаковать [архив](https://byfox.dev/data/multiwindowswitcher/MultiWindowSwitcher.zip)
2. Запустить `MultiWindowSwitcher.exe`
3. Навести мышь на иконку у правого края

Сначала посмотреть? [Демонстрация](https://byfox.dev/multiwindowswitcher/demo/?lang=ru) работает прямо в браузере.

Windows 10 / 11 (x64), нужен [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0). Бесплатно, открытый код, без телеметрии.

## Сборка из исходников

Нужен [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
git clone https://github.com/FoxUSA/MultiWindowSwitcher.git
cd MultiWindowSwitcher
powershell -ExecutionPolicy Bypass -File .\Data\build.ps1
```

Лаунчер `MultiWindowSwitcher.exe` появится в корне репозитория, программа — в `Data`, настройки — в `UserData`. Исходники: `Data/src/App` (программа, WPF + [WPF UI](https://github.com/lepoco/wpfui)), `Data/src/Launcher` (лаунчер).

## Лицензия

[GPL-3.0](LICENSE) — можно пользоваться, менять и распространять со ссылкой на автора ([NOTICE](NOTICE)); программы на основе этого кода тоже должны оставаться открытыми.
