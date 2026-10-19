// wav_io.cpp: see wav_io.h.
#include "wav_io.h"

#include <windows.h>

#include <cmath>
#include <cstdio>
#include <cstring>

namespace esp {
namespace {

uint32_t rd32(const unsigned char *p) { return p[0] | (p[1] << 8) | (p[2] << 16) | (static_cast<uint32_t>(p[3]) << 24); }
uint16_t rd16(const unsigned char *p) { return static_cast<uint16_t>(p[0] | (p[1] << 8)); }

bool readAll(const std::string &pathUtf8, std::vector<unsigned char> &bytes, std::string &err) {
    FILE *f = nullptr;
    if (_wfopen_s(&f, widen(pathUtf8).c_str(), L"rb") != 0 || f == nullptr) {
        err = "cannot open " + pathUtf8;
        return false;
    }
    std::fseek(f, 0, SEEK_END);
    const long size = std::ftell(f);
    std::fseek(f, 0, SEEK_SET);
    if (size < 0) {
        std::fclose(f);
        err = "cannot size " + pathUtf8;
        return false;
    }
    bytes.resize(static_cast<size_t>(size));
    const size_t got = size > 0 ? std::fread(bytes.data(), 1, bytes.size(), f) : 0;
    std::fclose(f);
    if (got != bytes.size()) {
        err = "short read on " + pathUtf8;
        return false;
    }
    return true;
}

} // namespace

std::wstring widen(const std::string &utf8) {
    if (utf8.empty()) return std::wstring();
    const int n = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n > 0 ? n : 0), L'\0');
    if (n > 0) MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), &w[0], n);
    return w;
}

bool readWav16(const std::string &pathUtf8, WavData &out, std::string &err) {
    std::vector<unsigned char> b;
    if (!readAll(pathUtf8, b, err)) return false;
    if (b.size() < 12 || std::memcmp(b.data(), "RIFF", 4) != 0 || std::memcmp(b.data() + 8, "WAVE", 4) != 0) {
        err = pathUtf8 + ": not a RIFF/WAVE file";
        return false;
    }
    bool haveFmt = false;
    int format = 0, channels = 0, rate = 0, bits = 0;
    size_t i = 12;
    while (i + 8 <= b.size()) {
        const uint32_t size = rd32(&b[i + 4]);
        const size_t body = i + 8;
        if (body + size > b.size()) {
            err = pathUtf8 + ": truncated chunk";
            return false;
        }
        if (std::memcmp(&b[i], "fmt ", 4) == 0 && size >= 16) {
            format = rd16(&b[body]);
            channels = rd16(&b[body + 2]);
            rate = static_cast<int>(rd32(&b[body + 4]));
            bits = rd16(&b[body + 14]);
            if (format == 0xFFFE && size >= 26) format = rd16(&b[body + 24]);  // extensible: sub-format GUID head
            haveFmt = true;
        } else if (std::memcmp(&b[i], "data", 4) == 0) {
            if (!haveFmt) {
                err = pathUtf8 + ": data before fmt";
                return false;
            }
            if (format != 1 || bits != 16 || channels < 1 || rate <= 0) {
                err = pathUtf8 + ": only 16-bit PCM is supported";
                return false;
            }
            out.sampleRate = rate;
            out.channels = channels;
            out.samples.resize(size / 2);
            std::memcpy(out.samples.data(), &b[body], out.samples.size() * 2);
            return true;
        }
        i = body + size + (size & 1);
    }
    err = pathUtf8 + ": no data chunk";
    return false;
}

std::vector<int16_t> firstChannel(const WavData &w) {
    std::vector<int16_t> m(w.frames());
    for (size_t f = 0; f < m.size(); ++f) m[f] = w.samples[f * static_cast<size_t>(w.channels)];
    return m;
}

bool writeWav16Mono(const std::string &pathUtf8, const float *samples, size_t n, int sampleRate, std::string &err) {
    FILE *f = nullptr;
    if (_wfopen_s(&f, widen(pathUtf8).c_str(), L"wb") != 0 || f == nullptr) {
        err = "cannot write " + pathUtf8;
        return false;
    }
    auto w32 = [&](uint32_t v) { std::fwrite(&v, 4, 1, f); };
    auto w16 = [&](uint16_t v) { std::fwrite(&v, 2, 1, f); };
    const uint32_t dataBytes = static_cast<uint32_t>(n * 2);
    std::fwrite("RIFF", 1, 4, f);
    w32(36 + dataBytes);
    std::fwrite("WAVE", 1, 4, f);
    std::fwrite("fmt ", 1, 4, f);
    w32(16);
    w16(1);
    w16(1);
    w32(static_cast<uint32_t>(sampleRate));
    w32(static_cast<uint32_t>(sampleRate * 2));
    w16(2);
    w16(16);
    std::fwrite("data", 1, 4, f);
    w32(dataBytes);
    std::vector<int16_t> chunk(4096);
    for (size_t at = 0; at < n;) {
        const size_t m = (n - at) < chunk.size() ? (n - at) : chunk.size();
        for (size_t k = 0; k < m; ++k) {
            float v = samples[at + k] * 32767.0f;
            if (v > 32767.0f) v = 32767.0f;
            if (v < -32768.0f) v = -32768.0f;
            chunk[k] = static_cast<int16_t>(std::lround(v));
        }
        std::fwrite(chunk.data(), 2, m, f);
        at += m;
    }
    const bool ok = std::ferror(f) == 0;
    std::fclose(f);
    if (!ok) err = "write error on " + pathUtf8;
    return ok;
}

} // namespace esp
