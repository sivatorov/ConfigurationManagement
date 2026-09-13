# Управление конфигурациями 1С

![Версия](https://img.shields.io/badge/Версия-0.3.7.20-1F6FEB) ![.NET](https://img.shields.io/badge/.NET-10-512BD4) ![Windows/WPF](https://img.shields.io/badge/Windows-WPF-4B8BBE) ![Linux/Avalonia](https://img.shields.io/badge/Linux-Avalonia%2011-8B5CF6) ![Лицензия](https://img.shields.io/badge/Лицензия-Open%20Source-success)

> **Кроссплатформенное десктопное приложение на .NET для управления информационными базами 1С:Предприятие 8.3**, заменяющее стандартный список баз 1С современным интерфейсом. Одна кодовая база собирается под обе ОС: **WPF** на Windows и **Avalonia 11** на Linux.

---

## ✨ Возможности

- **Запуск баз** в режимах «1С:Предприятие» и «Конфигуратор»; выбор версии и линии платформы, разрядности (32/64), тонкого/толстого клиента, веб-клиента; автопоиск установленной платформы.
- **Управление списком**: вкладки **Все базы / Избранное / Недавние**, иерархические группы с вложенностью, избранное `Alt+1…9`, закрепление, поиск и мультифильтр по тегам, сортировка.
- **Настраиваемые горячие клавиши** для запуска и действий интерфейса.
- **Создание ИБ** — пустая или из шаблона (`.cf`/`.dt`), файловые и клиент-серверные; редактирование, удаление, выгрузка `.dt`/`.cf`, тестирование, физическое удаление каталога.
- **Очистка кэша 1С** с отображением размеров по базам и остатков от удалённых баз.
- **Синхронизация с `ibases.v8i`** (импорт/экспорт, автоматическая по режиму), **импорт из StartManager**, резервные копии профиля.
- **Профили пользователей** с паролями (PBKDF2-SHA256), миграция данных.
- **Локализация** (русский/английский, подключение языков без пересборки).
- **Темы оформления**: светлая/тёмная, собственные цветовые схемы с двумя палитрами и живым предпросмотром.
- **Автообновление из GitHub Releases** с единым диалогом обновления.
- **Системный трей**, компактный режим интерфейса, виртуализация списков.

[![Infostart](https://infostart.ru/bitrix/templates/sandbox_empty/assets/tpl/abo/img/logo.svg)](https://infostart.ru/1c/articles/2764888/)

> 📌 **Публикация на Infostart:** [Управление конфигурациями 1С](https://infostart.ru/1c/articles/2764888/)

---

## 🖥️ Технологии

| Технология | Назначение |
|------------|------------|
| **.NET 10** | Платформа разработки (`net10.0-windows` / `net10.0`) |
| **WPF** | Графический интерфейс (Windows) |
| **Avalonia 11** | Графический интерфейс (Linux) |
| **MVVM** + **Microsoft.Extensions.DependencyInjection** | Архитектура и внедрение зависимостей |
| **MaterialDesignThemes** | Иконки и современный интерфейс (Windows) |
| **System.Text.Json** | Хранение данных в JSON-файлах |

---

## 📦 Требования

### Windows
- **Windows 10 / 11**
- **.NET 10 SDK** (для сборки из исходного кода)
- Установленная платформа **1С:Предприятие 8.3**

### Linux
- **.NET 10 SDK** (для сборки; self-contained публикация не требует runtime)
- Установленная платформа **1С:Предприятие 8.3 для Linux** (клиент `1cv8`)
- Зависимости пакета: GTK3, glib2, `xdg-utils`; для трея на GNOME Shell может потребоваться расширение **AppIndicator**

---

## 🔧 Сборка и запуск

```bash
git clone https://github.com/sivatorov/ConfigurationManagement.git
cd Configuration Management

# Сборка
dotnet build "Configuration Management/Configuration Management.csproj"

# Запуск
dotnet run --project "Configuration Management/Configuration Management.csproj"
```

Целевой TFM и набор файлов выбираются автоматически по ОС: на **Windows** — WPF (`net10.0-windows`), на **Linux** — Avalonia (`net10.0`). Также доступны скрипты [`build.sh`](Configuration Management/build.sh), [`build.ps1`](Configuration Management/build.ps1) и упаковка в single-file, AppImage и `.deb`.

---

## 🐧 Linux

Linux-порт выполнен и включает Avalonia UI, сервисы платформы 1С (`1cv8`, `readelf`, `/proc`), системный трей, сборку в AppImage и `.deb`. Целевые дистрибутивы — Ubuntu LTS, Debian, ALT Linux, Astra Linux (x64). Реализована диагностика окружения при старте (в т. ч. детектор виртуализации и программного рендера) и корректная работа безрамочных модальных окон, что повышает стабильность в виртуализированных средах и на оконных менеджерах X11. Подробнее — в [`Configuration Management/LINUX_PORT.md`](Configuration Management/LINUX_PORT.md).

```bash
# Сборка single-file linux-x64
./build.sh Release publish

# Упаковка в AppImage
./package/linux/appimage.sh

# Упаковка в .deb
./package/linux/deb/build-deb.sh
```

---

## 💾 Хранение данных

Данные сохраняются в `%APPDATA%\ConfigurationManagement` (на Linux — `~/.config/ConfigurationManagement`): `infobases.json` (базы), `groups.json` (группы), `settings.json` (настройки интерфейса), `ColorSchemes/` (цветовые схемы), `logs/` (журнал приложения).

---

## 🧩 Архитектура

Приложение построено по паттерну **MVVM** (Models / ViewModels / Views / Services) с единой кодовой базой для Windows и Linux. Подробности — в [`ARCHITECTURE.md`](ARCHITECTURE.md). Полная история изменений — в [`CHANGELOG.md`](CHANGELOG.md).

---

## 📜 Лицензия

Проект распространяется с **открытым исходным кодом** и доступен бесплатно. Исходный код размещён в этом репозитории GitHub и в разделе **Файлы** публикации на [Infostart](https://infostart.ru/1c/articles/2764888/). Подробнее — в [`LICENSE`](LICENSE).

---

## 🤝 Вклад и контакты

Буду рад любым улучшениям — сделайте форк, создайте ветку и отправьте pull request. По вопросам и предложениям создавайте [issue](https://github.com/sivatorov/ConfigurationManagement/issues) в репозитории.
