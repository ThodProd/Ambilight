#pragma once
// Первая сборка: обычный ESP32 DevKit / ESP32-WROOM-32. Для другой платы проверьте GPIO.
// Никаких сторонних библиотек: используется Arduino-ESP32 3.3.4 и его RMT.
#define LED_PIN 18
#define BUTTON_PIN 23
// Энкодер замыкает A/B на GND. Поставьте -1, если энкодер пока не подключён.
#define ENCODER_A_PIN -1
#define ENCODER_B_PIN -1
#define MAX_LEDS 360
#define DEFAULT_LEDS 96
// WS2812B: 3 байта GRB. Для RGB SK6812 задайте требуемый порядок.
// Для RGBW SK6812 поставьте LED_CHANNELS=4; четвёртый канал будет нулевым.
#define LED_CHANNELS 3
#define CHANNEL_R 1
#define CHANNEL_G 0
#define CHANNEL_B 2
#define FIRMWARE_VERSION "1.0.6"
