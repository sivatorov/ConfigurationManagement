## [0.3.9.63] — 2026-09-25

Срочный выпуск: исправление регрессии из 0.3.9.62 — приложение снова запускается на Windows
([#298](https://github.com/sivatorov/ConfigurationManagement/issues/298)).

### Исправлено

- **Windows: приложение снова стартует — убрано DynamicResource из BasedOn у кнопок
  управления окном ([#298](https://github.com/sivatorov/ConfigurationManagement/issues/298))** —
  в 0.3.9.62 кнопки «Свернуть/Развернуть/Закрыть» главного окна наследовались через
  `BasedOn="{DynamicResource ...}"`, а WPF не допускает `DynamicResource` в свойстве `BasedOn`
  у `Style` (это CLR-свойство, а не DependencyProperty — `DynamicResource` работает только
  с DependencyProperty). При старте возникала
  `System.Windows.Markup.XamlParseException` («DynamicResourceExtension невозможно задать
  в свойстве BasedOn типа Style»), и приложение не запускалось.

  Исправление: ссылки в `BasedOn` переведены на `StaticResource` — стили
  `WindowControlButton` / `WindowControlCloseButton` определены в `App.xaml`, загружаются
  до главного окна и не зависят от темы, поэтому статическая ссылка корректна. Компактность
  кнопок окна (компактный режим, [#296](https://github.com/sivatorov/ConfigurationManagement/issues/296),
  46×34 → 44×30) и остальное поведение сохранены без изменений.

- Linux/Avalonia версия не затрагивалась: кнопки окна там создаются программно
  и в WPF-разметке не участвуют.

### Файлы для установки

- **Windows (x64)**: `ConfigurationManagement.exe` (single-file, self-contained).
- **Linux (x64)**: `ConfigurationManagement-linux-x64` (single-file, self-contained; при необходимости `chmod +x`).

---

Спасибо [@7OH](https://github.com/7OH) за сообщение о проблеме
([#298](https://github.com/sivatorov/ConfigurationManagement/issues/298)) и подтверждение,
что Linux-версия не затронута.