// esp_log.cpp: see esp_log.h.
#include "esp_log.h"

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>

namespace esp {
namespace {

std::mutex g_lock;
std::deque<std::string> g_lines;
constexpr size_t kMaxLines = 512;  // a runaway logger must not grow without bound
size_t g_dropped = 0;

const char *levelName(LogLevel l) {
    switch (l) {
    case LogLevel::Warn: return "warn";
    case LogLevel::Error: return "error";
    default: return "info";
    }
}

} // namespace

void log(LogLevel level, const std::string &line) {
    std::string s = "[esp] ";
    s += levelName(level);
    s += ": ";
    s += line;
    s += '\n';
    std::lock_guard<std::mutex> lk(g_lock);
    if (g_lines.size() >= kMaxLines) {
        g_lines.pop_front();
        ++g_dropped;
    }
    g_lines.push_back(std::move(s));
}

void logf(LogLevel level, const char *fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    log(level, buf);
}

int32_t pollLog(char *dst, int32_t capacity) {
    if (dst == nullptr || capacity <= 1) return 0;
    std::lock_guard<std::mutex> lk(g_lock);
    int32_t n = 0;
    if (g_dropped > 0) {
        char note[96];
        const int len = std::snprintf(note, sizeof note, "[esp] warn: %zu log lines dropped (queue full)\n", g_dropped);
        if (len > 0 && len < capacity - 1) {
            std::memcpy(dst, note, static_cast<size_t>(len));
            n = len;
            g_dropped = 0;
        }
    }
    while (!g_lines.empty()) {
        const std::string &s = g_lines.front();
        if (n + static_cast<int32_t>(s.size()) >= capacity) {
            if (n == 0) {  // a single line longer than the buffer: truncate it rather than block the queue
                const size_t m = static_cast<size_t>(capacity - 2);
                std::memcpy(dst, s.data(), m);
                dst[m] = '\n';
                n = static_cast<int32_t>(m + 1);
                g_lines.pop_front();
            }
            break;
        }
        std::memcpy(dst + n, s.data(), s.size());
        n += static_cast<int32_t>(s.size());
        g_lines.pop_front();
    }
    dst[n] = '\0';
    return n;
}

} // namespace esp
