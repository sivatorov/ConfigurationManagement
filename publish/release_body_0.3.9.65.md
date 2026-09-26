## [0.3.9.65] — 2026-09-26

Выпуск: стабилизирован фокус на поле «Наименование» в окне добавления/редактирования группы
([#297](https://github.com/sivatorov/ConfigurationManagement/issues/297)).

### Исправлено

- **Фокус на поле «Наименование» в окне добавления/редактирования группы
  на обеих платформах ([#297](https://github.com/sivatorov/ConfigurationManagement/issues/297))** —
  после фикса 0.3.9.57 вкладка «Основные» открывается активной, но курсор в поле
  «Наименование» попадал не всегда: на Windows/WPF фокус ставился синхронно в `Loaded`,
  и TabControl при инициализации мог перехватить его; на Linux/Avalonia вызов `Focus()`
  в `Opened` происходил без выделения текста и без проверки результата (мог вернуть `false`,
  если окно ещё не активировано или не завершилась компоновка).

  Теперь фокус ставится отложенно — после полного показа и активации окна
  (`Dispatcher.BeginInvoke` с приоритетом `ApplicationIdle` в WPF,
  `Dispatcher.UIThread.Post` с приоритетом `Background` в Avalonia) — с выделением текста
  целиком (`SelectAll`), а в Avalonia при неудачном `Focus()` выполняется до 3 повторов.
  Поведение служебных узлов «Без группы»/«Закреплённые» (открытие на вкладке «Цвет»,
  поле имени заблокировано) не менялось.

### Файлы для установки

- **Windows (x64)**: `ConfigurationManagement.exe` (single-file, self-contained).
- **Linux (x64)**: `ConfigurationManagement` (single-file, self-contained; при необходимости `chmod +x`).

---

Спасибо за сообщение о проблеме
([#297](https://github.com/sivatorov/ConfigurationManagement/issues/297)).