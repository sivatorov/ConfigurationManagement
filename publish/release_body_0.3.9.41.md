## [0.3.9.41] — 2026-09-24

Исправлен открытый issue [#282](https://github.com/sivatorov/ConfigurationManagement/issues/282).

### Исправления

- **Подпись «Утилиты» на кнопке подменю в Windows ([#282](https://github.com/sivatorov/ConfigurationManagement/issues/282))** —
  на верхней панели Windows кнопка «Утилиты» показывала только иконку без текста, тогда как на Linux
  у кнопки есть и иконка, и текст «Утилиты». Теперь в Windows рядом с иконкой выводится подпись.
  1. **Кнопка `UtilitiesButton`** (`MainWindow.xaml`): вместо одной иконки — горизонтальный `StackPanel`
     с иконкой `Apps` (16×16) и текстом `Main.Utilities` (13 px, вторичный цвет), отступы скорректированы.
  2. **Локализация**: используется существующий ключ `Main.Utilities` («Утилиты»/«Utilities») —
     новых ключей не требуется. Поведение и команды меню не изменены.

### Файлы для установки

- **Windows (x64)**: `ConfigurationManagement.exe` (single-file, self-contained).
- **Linux (x64)**: `ConfigurationManagement-linux-x64` (single-file, self-contained; при необходимости `chmod +x`).