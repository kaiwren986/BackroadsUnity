// esp_log.h: the plugin's log queue (esp_poll_log). Any thread may write; the main thread polls. Never called from
// esp_read_audio (the audio thread does not log).
#pragma once

#include <cstdint>
#include <string>

namespace esp {

enum class LogLevel { Info, Warn, Error };

void log(LogLevel level, const std::string &line);
void logf(LogLevel level, const char *fmt, ...);
// Moves queued lines ("[esp] warn: ...\n") into dst; returns bytes written (0 = nothing queued).
int32_t pollLog(char *dst, int32_t capacity);

} // namespace esp
