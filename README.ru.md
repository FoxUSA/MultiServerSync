<div align="center">

<img src="images/fox.png" width="96" alt="Логотип MultiServerSync">

# MultiServerSync

**Файлы и команды на все серверы сразу.**

Отмечаете серверы — и одной кнопкой заливаете файлы или выполняете команду на всех. Каждый сервер отчитывается отдельно.

[![Скачать .zip](https://img.shields.io/badge/Скачать-multiserversync.zip-2ea44f?style=for-the-badge)](https://byfox.dev/data/multiserversync/multiserversync.zip)
[![Сайт](https://img.shields.io/badge/Сайт-byfox.dev-4ecdc4?style=for-the-badge)](https://byfox.dev/multiserversync/)

![Windows 10 / 11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?logo=windows)
![SSH / SFTP](https://img.shields.io/badge/SSH%20%2F%20SFTP-Linux--серверы-444)
![Бесплатно](https://img.shields.io/badge/цена-бесплатно-2ea44f)

[English](README.md) · **Русский**

</div>

<p align="center">
<a href="images/multiserversync-copy.png"><img src="images/multiserversync-copy.png" height="140" alt="Копирование"></a>
<a href="images/multiserversync-command.png"><img src="images/multiserversync-command.png" height="140" alt="Команда"></a>
<a href="images/multiserversync-servers.png"><img src="images/multiserversync-servers.png" height="140" alt="Серверы"></a>
<a href="images/multiserversync-tabs.png"><img src="images/multiserversync-tabs.png" height="140" alt="Все вкладки"></a>
</p>

Серверы заводятся один раз. Дальше — отметили нужные, выбрали файлы или набрали команду и нажали **Выполнить**.

- **Копирование на все серверы** — файлы и папки уходят на отмеченные серверы параллельно. Обрыв связи не оставит обрезка — файл подменяется целиком. Упавшие серверы повторяются одной кнопкой.
- **Только изменённое** — сверка по дате или по контрольной сумме SHA-256: совпавшие файлы не отправляются. После копирования суммы можно сверить ещё раз.
- **Права, владелец и дата на месте** — заменённый файл сохраняет права и владельца, новый берёт их у папки, в которую ложится. Дата изменения переносится с локального файла.
- **Команды и скрипты** — команда или многострочный скрипт любой длины на всех серверах сразу, при желании через sudo. Избранные команды и история под рукой.
- **Вывод каждого сервера** — вживую и по каждому серверу отдельно, с подсветкой. Зависшую команду прерывают Ctrl+C или снимают прямо из программы.
- **Вкладки под задачи** — у каждой вкладки свои серверы, файлы, путь и команда. **Выполнить везде** запускает все вкладки разом, итог виден на ярлыках.

## Как начать

1. Распаковать [архив](https://byfox.dev/data/multiserversync/multiserversync.zip)
2. Запустить `MultiServerSync.exe`
3. Добавить серверы — и отметить, куда отправлять

Windows 10 / 11 (x64), нужен [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0). Бесплатно, без телеметрии. В репозитории — только страница и ссылки на загрузку.

---

<sub>Ключевые слова: SSH на много серверов, синхронизация по SFTP, заливка файлов на несколько серверов, выполнение команды на всех серверах, параллельный SSH, SSH-утилита для Windows.</sub>
