# Компоненты

- Microsoft .NET / Windows Desktop Runtime 10.0.12: включён в Windows-пакет; лицензия и notices находятся рядом с runtime в worker.
- Native capture: собран LLVM-MinGW; лицензии runtime находятся в licenses.
- Прошивка: Arduino-ESP32 3.3.4 (LGPL-2.1), ESP-IDF из соответствующего официального пакета (Apache-2.0 и лицензии включённых компонентов). Исходники ядра и лицензии: https://github.com/espressif/arduino-esp32/tree/3.3.4 и https://github.com/espressif/esp-idf. Исходники самой прошивки и параметры сборки включены в пакет. Полный ELF и промежуточные объектные файлы сохраняются локально при сборке.
- Протокол/интерфейс Ambilight не использует сторонние LED- или JSON-библиотеки для Arduino.

Инструменты компиляции и пользовательская flash ESP32 в дистрибутив Windows не включаются.
