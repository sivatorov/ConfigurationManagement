## [0.3.9.43] — 2026-09-24

Реализовано пожелание из issue [#285](https://github.com/sivatorov/ConfigurationManagement/issues/285).

### Новое

- **«Найти в списке» — переход к базе в общем списке ([#285](https://github.com/sivatorov/ConfigurationManagement/issues/285))** —
  из любой вкладки («Избранное», «Недавние», закреплённые) можно перейти к базе в общем списке
  «Все базы»: раскрываются группы от корня до группы базы, база выделяется и попадает в видимую область.
  1. **Команда `FindInListCommand`**: переключает вкладку на «Все базы», сбрасывает поиск и фильтр тегов,
     принудительно раскрывает цепочку групп-предков и выделяет базу (UI сам прокручивает список к строке).
     На обеих платформах (Windows/WPF и Linux/Avalonia).
  2. **Горячая клавиша Ctrl+T** (настраиваемая в «Настройки → Клавиши») — новый ключ настроек `HotkeyFindInList`.
  3. **Пункт контекстного меню базы «Найти в списке»** (иконка лупы) — Windows/WPF и Linux/Avalonia.
  4. Новые ключи локализации `Main.FindInList`, `Settings.Hotkeys.FindInList` в ru/en.

### Файлы для установки

- **Windows (x64)**: `ConfigurationManagement.exe` (single-file, self-contained).
- **Linux (x64)**: `ConfigurationManagement-linux-x64` (single-file, self-contained; при необходимости `chmod +x`).